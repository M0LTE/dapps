using dapps.client.Backhaul;
using dapps.client.Discovery;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Pulls pending outbound messages from the queue, computes residual
/// TTL, resolves a route for each, and hands them to a matching
/// <see cref="IDappsBackhaul"/> for delivery. Owns queue/router
/// concerns — does not know about streams, AGW frames, or session
/// protocols; those live behind the backhaul seam (Plan A0).
///
/// Plan B4 routing precedence (first match wins):
///   1. Manual <see cref="DbNeighbour"/> with matching base callsign
///      — explicit operator override.
///   2. Fresh <see cref="DbDiscoveredPeer"/> rows for that base
///      callsign, sorted by <c>CostHint</c> ascending — pick the
///      cheapest channel the peer's been heard on.
///   3. Hand-maintained <see cref="DbRouteHint"/> next-hop fallback.
///   4. None of the above → leave the message in queue until its
///      ttl expires, log "no route".
/// </summary>
public class OutboundMessageManager(
    Database database,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<SystemOptions> options,
    IEnumerable<IDappsBackhaul> backhauls,
    OperationalMetrics? metrics = null)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<OutboundMessageManager>();
    private readonly IReadOnlyList<IDappsBackhaul> backhauls = backhauls.ToList();
    private readonly OperationalMetrics metrics = metrics ?? new OperationalMetrics();

    public async Task DoRun(CancellationToken stoppingToken = default)
    {
        logger.LogInformation("Starting a run");

        var optionsValue = options.CurrentValue;
        var messages = await database.GetPendingOutboundMessages();
        var neighbours = await database.GetNeighbours();
        var peers = await database.GetDiscoveredPeers();

        foreach (var message in messages)
        {
            var residualTtl = TtlMath.Residual(message.Ttl, message.CreatedAt, DateTime.UtcNow);
            if (residualTtl is <= 0)
            {
                logger.LogWarning("Dropping message {0} for {1}: ttl expired ({2}s queued, original ttl={3}s)",
                    message.Id, message.Destination,
                    (int)(DateTime.UtcNow - message.CreatedAt).TotalSeconds, message.Ttl);
                metrics.RecordTtlExpired(message.Id, message.Destination);
                await database.DeleteMessage(message.Id);
                continue;
            }

            var route = await ResolveRoute(message, neighbours, peers, optionsValue);
            if (route is null)
            {
                logger.LogWarning("No route for {0}, leaving in queue", message.Id);
                metrics.RecordNoRoute(message.Id, message.Destination);
                continue;
            }

            var bm = new BackhaulMessage(
                Id: message.Id,
                Destination: message.Destination,
                Salt: message.Salt,
                Ttl: residualTtl,
                Payload: message.Payload);

            var backhaul = this.backhauls.FirstOrDefault(b => b.CanHandle(route));
            if (backhaul is null)
            {
                logger.LogError(
                    "No backhaul accepts route to {0} (BpqPort={1}, UdpEndpoint={2}). Skipping {3}.",
                    route.Callsign, route.BpqPort, route.UdpEndpoint, message.Id);
                continue;
            }

            var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
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

    private async Task<BackhaulRoute?> ResolveRoute(
        DbMessage message,
        ICollection<DbNeighbour> neighbours,
        IReadOnlyList<DbDiscoveredPeer> peers,
        SystemOptions optionsValue)
    {
        // Destinations are `app@call[-ssid]`. Compare on the base
        // callsign — SSID mismatches between configured route and
        // destination are tolerated.
        var destBaseCall = message.Destination.Split('@').Last().Split('-')[0];

        // 1. Manual operator-configured neighbour wins. Lets a sysop
        //    pin a specific route even if discovery would suggest a
        //    different (and possibly cheaper) channel.
        var manual = neighbours.FirstOrDefault(
            n => n.Callsign.Split('-')[0].Equals(destBaseCall, StringComparison.OrdinalIgnoreCase));
        if (manual is not null)
        {
            return new BackhaulRoute(
                manual.Callsign,
                BpqPort: manual.BpqPort ?? optionsValue.DefaultBpqPort,
                UdpEndpoint: manual.UdpEndpoint);
        }

        // 2. Discovered peers, freshness-filtered, ordered by cost.
        //    The cheapest fresh channel wins.
        var now = DateTime.UtcNow;
        var freshPeer = peers
            .Where(p => p.Callsign.Split('-')[0].Equals(destBaseCall, StringComparison.OrdinalIgnoreCase))
            .Where(p => (now - p.LastSeen).TotalSeconds <= p.TtlSeconds)
            .OrderBy(p => p.CostHint)
            .ThenBy(p => p.Hops)
            .FirstOrDefault();
        if (freshPeer is not null)
        {
            logger.LogInformation(
                "Routing {0} to {1} via discovered peer on {2}/{3} (cost={4}, hops={5})",
                message.Id, message.Destination,
                freshPeer.Bearer, freshPeer.ChannelKey,
                freshPeer.CostHint, freshPeer.Hops);
            return new BackhaulRoute(
                freshPeer.Callsign,
                BpqPort: freshPeer.BpqPort ?? optionsValue.DefaultBpqPort,
                UdpEndpoint: freshPeer.UdpEndpoint);
        }

        // 3. Hand-maintained route hint. The fallback for "I know peer
        //    X is reachable via my neighbour Y" without a discovery
        //    record. Phase B5 (flood-and-learn) may eventually obsolete
        //    this, but it stays useful for explicit operator overrides.
        var routeHint = await database.GetRouteHint(destBaseCall) ?? await database.GetRouteHint("*");
        if (routeHint is not null)
        {
            var nextHop = await database.GetNeighbour(routeHint.NextHop);
            if (nextHop is not null)
            {
                logger.LogInformation("Routing {0} for {1} via route-hint next-hop {2}",
                    message.Id, message.Destination, nextHop.Callsign);
                return new BackhaulRoute(
                    nextHop.Callsign,
                    BpqPort: nextHop.BpqPort ?? optionsValue.DefaultBpqPort,
                    UdpEndpoint: nextHop.UdpEndpoint);
            }
        }

        return null;
    }
}
