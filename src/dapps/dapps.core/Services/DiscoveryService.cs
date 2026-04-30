using dapps.client.Discovery;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Runs the registered <see cref="IDiscoveryBearer"/>s. For each:
///
///   1. Periodically (every <c>DiscoveryBeaconIntervalSeconds</c>)
///      emits our own beacon.
///   2. Concurrently iterates the bearer's listen stream; for every
///      received beacon, upserts a <see cref="DbDiscoveredPeer"/> row
///      stamped with the bearer + LastSeen=now.
///
/// A separate sweep ages out peer rows whose freshness window
/// (the beacon's advertised <c>ttl</c>) has elapsed.
///
/// The service is itself the only place that knows which bearers are
/// configured — Program.cs hands them in, the service runs them
/// uniformly. New bearers (MeshCore companion, KISS, …) get added
/// without changing this file.
/// </summary>
public sealed class DiscoveryService(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Bearers materialised at <see cref="ExecuteAsync"/> time
    /// rather than via constructor injection — see comment on
    /// <c>BuildBearers</c> for why.</summary>
    private IReadOnlyList<IDiscoveryBearer> BuildBearers()
    {
        var opts = options.CurrentValue;
        var bearers = new List<IDiscoveryBearer>();

        if (!string.IsNullOrWhiteSpace(opts.MulticastGroup))
        {
            try
            {
                bearers.Add(new UdpMulticastDiscoveryBearer(opts.MulticastGroup, opts.Callsign, loggerFactory));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "DiscoveryService: cannot construct UdpMulticastDiscoveryBearer for '{0}' — multicast disabled",
                    opts.MulticastGroup);
            }
        }

        if (opts.AgwDiscovery)
        {
            bearers.Add(new AgwUiDiscoveryBearer(
                opts.NodeHost, opts.AgwPort,
                opts.Callsign, opts.DefaultBpqPort, loggerFactory));
        }

        return bearers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Construction is deferred until ExecuteAsync runs (after
        // DbStartup has created the systemoptions table) so the bearer
        // factories can safely call CurrentValue on the options monitor.
        var bearers = BuildBearers();
        if (bearers.Count == 0)
        {
            logger.LogInformation("DiscoveryService: no bearers configured — discovery disabled");
            return;
        }

        // Bring each bearer online; if a bearer fails to start, log and
        // skip — the service still runs whatever bearers did start.
        var live = new List<IDiscoveryBearer>();
        foreach (var b in bearers)
        {
            try
            {
                await b.StartAsync(stoppingToken);
                live.Add(b);
                logger.LogInformation("DiscoveryService: started bearer '{0}'", b.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DiscoveryService: bearer '{0}' failed to start; skipping", b.Name);
            }
        }
        if (live.Count == 0) return;

        // Listen loop per bearer — each yields beacons into the DB on its own task.
        var listenTasks = live.Select(b => Task.Run(() => ListenLoopAsync(b, stoppingToken), stoppingToken)).ToList();

        // Beacon emit loop + sweep loop on this task.
        try
        {
            await EmitAndSweepAsync(live, stoppingToken);
        }
        finally
        {
            try { await Task.WhenAll(listenTasks); } catch { /* expected on shutdown */ }
            foreach (var b in live)
            {
                try { await b.DisposeAsync(); } catch { /* best effort */ }
            }
        }
    }

    private async Task EmitAndSweepAsync(IReadOnlyList<IDiscoveryBearer> live, CancellationToken stoppingToken)
    {
        var nextSweep = DateTime.UtcNow + SweepInterval;
        // Emit immediately at startup so a freshly-joined node is visible
        // to its neighbours within seconds rather than a full interval.
        await EmitOnceAsync(live, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.DiscoveryBeaconIntervalSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            await EmitOnceAsync(live, stoppingToken);

            if (DateTime.UtcNow >= nextSweep)
            {
                try
                {
                    var aged = await database.AgeOutDiscoveredPeers(DateTime.UtcNow);
                    if (aged > 0) logger.LogInformation("DiscoveryService: aged out {0} stale peer(s)", aged);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiscoveryService: age-out sweep failed");
                }
                nextSweep = DateTime.UtcNow + SweepInterval;
            }
        }
    }

    private async Task EmitOnceAsync(IReadOnlyList<IDiscoveryBearer> live, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var beacon = new BeaconFrame(
            Callsign: opts.Callsign,
            Hops: 0,
            // Advertised freshness is 3× the beacon interval so a peer
            // tolerates losing two beacons before aging us out.
            Ttl: Math.Max(60, opts.DiscoveryBeaconIntervalSeconds * 3),
            // The Bearer field on an outgoing beacon doesn't matter —
            // the receiver fills it in based on which bearer it heard
            // us on. Using AGW BPQ port 0 as a placeholder.
            Bearer: new AgwBearerHint(0));

        foreach (var b in live)
        {
            try
            {
                await b.AnnounceAsync(beacon, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DiscoveryService: bearer '{0}' announce failed", b.Name);
            }
        }
    }

    private async Task ListenLoopAsync(IDiscoveryBearer bearer, CancellationToken ct)
    {
        try
        {
            await foreach (var beacon in bearer.ListenAsync(ct).WithCancellation(ct))
            {
                try
                {
                    await UpsertAsync(beacon);
                    logger.LogInformation(
                        "DiscoveryService: heard {0} via {1} (hops={2}, ttl={3}s)",
                        beacon.Callsign, beacon.Bearer.Kind, beacon.Hops, beacon.Ttl);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiscoveryService: upsert failed for {0}", beacon.Callsign);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task UpsertAsync(BeaconFrame beacon)
    {
        var row = new DbDiscoveredPeer
        {
            Callsign = beacon.Callsign,
            Bearer = beacon.Bearer.Kind,
            Hops = beacon.Hops,
            TtlSeconds = beacon.Ttl,
            BpqPort = beacon.Bearer is AgwBearerHint a ? a.BpqPort : null,
            UdpEndpoint = beacon.Bearer is UdpBearerHint u ? u.Endpoint : null,
            LastSeen = DateTime.UtcNow,
        };
        await database.UpsertDiscoveredPeer(row);
    }
}
