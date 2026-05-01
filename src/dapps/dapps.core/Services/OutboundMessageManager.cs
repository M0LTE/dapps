using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Pulls pending outbound messages from the queue, computes residual
/// TTL, asks the configured <see cref="IRoutingAlgorithm"/> for a
/// route, and hands each message to a matching <see cref="IDappsBackhaul"/>
/// for delivery. Owns queue / dispatch concerns; routing strategy
/// itself lives behind <see cref="IRoutingAlgorithm"/> (B5 seam) so
/// algorithms (static, passive-learning, AODV-flood, NET-ROM-style,
/// MeshCore-style, …) can be swapped without touching this code.
///
/// The forward-outcome and inbound observation hooks are also routed
/// through the algorithm so it can update its internal state (learned
/// routes, failure counters, sequence numbers).
/// </summary>
public class OutboundMessageManager(
    Database database,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<SystemOptions> options,
    IEnumerable<IDappsBackhaul> backhauls,
    IRoutingAlgorithm routingAlgorithm,
    IRoutingContext routingContext,
    OperationalMetrics? metrics = null)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<OutboundMessageManager>();
    private readonly IReadOnlyList<IDappsBackhaul> backhauls = backhauls.ToList();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();

    /// <summary>
    /// Mutex on <see cref="DoRun"/> so concurrent triggers
    /// (background ticker + manual <c>POST /Message/dorun</c>, or two
    /// manual POSTs in flight) don't race through the same pending
    /// list and double-send. Calls that arrive while a run is
    /// in-flight return immediately — whatever's pending will be
    /// picked up on the next tick anyway.
    /// </summary>
    private readonly SemaphoreSlim runLock = new(1, 1);

    /// <summary>
    /// Internal counter incremented at the start of each *actually
    /// executed* run (skipped contended calls don't bump it). Used by
    /// the auto-forwarder integration test to verify the background
    /// service is ticking; not exposed to operators.
    /// </summary>
    internal int RunCount;

    public async Task DoRun(CancellationToken stoppingToken = default)
    {
        if (!await runLock.WaitAsync(0, stoppingToken))
        {
            logger.LogDebug("DoRun skipped: another run is already in flight");
            return;
        }
        try
        {
            await DoRunCore(stoppingToken);
        }
        finally
        {
            runLock.Release();
        }
    }

    private async Task DoRunCore(CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref RunCount);
        logger.LogInformation("Starting a run");

        var optionsValue = options.CurrentValue;
        var messages = await database.GetPendingOutboundMessages();

        foreach (var message in messages)
        {
            var residualTtl = TtlMath.Residual(message.Ttl, message.CreatedAt, DateTime.UtcNow);
            if (residualTtl is <= 0)
            {
                logger.LogWarning("Dropping message {0} for {1}: ttl expired ({2}s queued, original ttl={3}s)",
                    message.Id, message.Destination,
                    (int)(DateTime.UtcNow - message.CreatedAt).TotalSeconds, message.Ttl);
                metrics.RecordTtlExpired(message.Id, message.Destination);
                await database.SoftDeleteMessage(message.Id, "ttl-expired");
                continue;
            }

            var decision = await routingAlgorithm.ResolveAsync(message, routingContext, stoppingToken);
            BackhaulRoute route;
            switch (decision)
            {
                case RouteDecision.NextHop nh:
                    route = nh.Route;
                    break;
                case RouteDecision.Unreachable:
                    logger.LogWarning("No route for {0}, leaving in queue", message.Id);
                    metrics.RecordNoRoute(message.Id, message.Destination);
                    continue;
                default:
                    // Future RouteDecision cases (SourceRoute, BearerDelegated,
                    // FloodToNeighbours) will land here when those algorithms
                    // ship; today they don't appear, so a defensive log keeps
                    // a regression visible.
                    logger.LogError("Unsupported RouteDecision {0} for {1}; treating as Unreachable",
                        decision.GetType().Name, message.Id);
                    metrics.RecordNoRoute(message.Id, message.Destination);
                    continue;
            }

            // F1: preserve the originating callsign verbatim across re-forwards.
            // Empty means we don't know — outbound omits src= rather than lying
            // (e.g. claiming the link source is the originator).
            var originator = string.IsNullOrEmpty(message.OriginatorCallsign)
                ? null
                : message.OriginatorCallsign;

            var bm = new BackhaulMessage(
                Id: message.Id,
                Destination: message.Destination,
                Salt: message.Salt,
                Ttl: residualTtl,
                Payload: message.Payload,
                Originator: originator);

            var backhaul = this.backhauls.FirstOrDefault(b => b.CanHandle(route));
            if (backhaul is null)
            {
                logger.LogError(
                    "No backhaul accepts route to {0} (BpqPort={1}, UdpEndpoint={2}). Skipping {3}.",
                    route.Callsign, route.BpqPort, route.UdpEndpoint, message.Id);
                continue;
            }

            var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
            await routingAlgorithm.ObserveForwardOutcomeAsync(message, route, result, routingContext, stoppingToken);
            if (result.Accepted)
            {
                logger.LogInformation("Remote end accepted message {0} (via {1})", message.Id, backhaul.GetType().Name);
                metrics.RecordForwardSuccess(route.Callsign, message.Payload.Length);
                await database.MarkMessageAsForwarded(message.Id);
            }
            else
            {
                logger.LogError("Failed to forward message {0} to {1} via {2}: {3}",
                    message.Id, route.Callsign, backhaul.GetType().Name, result.Error);
                metrics.RecordForwardFailure(route.Callsign, message.Payload.Length, result.Error);
            }
        }
    }
}
