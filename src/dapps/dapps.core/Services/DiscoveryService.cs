using dapps.client.Discovery;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Hosted service that runs the per-channel discovery loop:
///
///   1. Reads <see cref="DbDiscoveryChannel"/> rows from the database
///      at start-up. Groups rows by bearer kind ("agw", "udp", …).
///   2. For each bearer kind that has at least one enabled channel,
///      constructs the matching <see cref="IDiscoveryBearer"/> and
///      starts it with that bearer's channel set.
///   3. For each enabled channel, schedules a per-channel beacon
///      timer at the channel's <c>BeaconIntervalSeconds</c>.
///   4. Iterates each bearer's listen stream concurrently. For every
///      received beacon, upserts a <see cref="DbDiscoveredPeer"/> row
///      stamped with the channel's <c>LinkClass</c> and <c>CostHint</c>
///      so the resolver can sort candidates by cost without joining.
///
/// New bearers (MeshCore, KISS) get a one-line addition to the
/// bearer-construction switch — the rest of the daemon is bearer-
/// agnostic.
/// </summary>
public sealed class DiscoveryService(
    Database database,
    IOptionsMonitor<SystemOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often to re-check the channels table when no
    /// channels are configured yet — operators add them via REST while
    /// the service is running, so we can't just give up at start.</summary>
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for at least one enabled channel to be configured. The
        // controllers can POST channels at any time after the service
        // boots, so we poll the table rather than reading once and
        // exiting.
        List<DbDiscoveryChannel> rows;
        while (true)
        {
            rows = (await database.GetDiscoveryChannels()).Where(r => r.Enabled).ToList();
            if (rows.Count > 0) break;
            logger.LogInformation(
                "DiscoveryService: no channels configured yet — polling every {0}s",
                (int)StartupPollInterval.TotalSeconds);
            try { await Task.Delay(StartupPollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        var byBearer = rows.GroupBy(r => r.Bearer.Trim().ToLowerInvariant()).ToList();
        var bearers = new Dictionary<string, IDiscoveryBearer>(StringComparer.Ordinal);
        var channelsByBearer = new Dictionary<string, IReadOnlyList<DiscoveryChannelInfo>>(StringComparer.Ordinal);

        foreach (var group in byBearer)
        {
            var bearerName = group.Key;
            var bearer = TryConstructBearer(bearerName);
            if (bearer is null)
            {
                logger.LogWarning("DiscoveryService: unknown bearer '{0}' — skipping {1} channel(s)",
                    bearerName, group.Count());
                continue;
            }
            var info = group.Select(r => new DiscoveryChannelInfo(
                r.Id, r.Bearer, r.ChannelKey, r.LinkClass,
                r.BeaconIntervalSeconds, r.AdvertisedTtlSeconds, r.CostHint)).ToList();
            try
            {
                await bearer.StartAsync(info, stoppingToken);
                bearers[bearerName] = bearer;
                channelsByBearer[bearerName] = info;
                logger.LogInformation(
                    "DiscoveryService: started bearer '{0}' with {1} channel(s)",
                    bearerName, info.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DiscoveryService: bearer '{0}' failed to start", bearerName);
                try { await bearer.DisposeAsync(); } catch { /* best effort */ }
            }
        }
        if (bearers.Count == 0) return;

        var listenTasks = bearers.Select(kv =>
            Task.Run(() => ListenLoopAsync(kv.Value, stoppingToken), stoppingToken)).ToList();

        try
        {
            await EmitAndSweepAsync(bearers, channelsByBearer, stoppingToken);
        }
        finally
        {
            try { await Task.WhenAll(listenTasks); } catch { /* expected on shutdown */ }
            foreach (var b in bearers.Values)
            {
                try { await b.DisposeAsync(); } catch { /* best effort */ }
            }
        }
    }

    private IDiscoveryBearer? TryConstructBearer(string bearerName)
    {
        var opts = options.CurrentValue;
        return bearerName switch
        {
            "agw" => new AgwUiDiscoveryBearer(opts.NodeHost, opts.AgwPort, opts.Callsign, loggerFactory),
            "udp" => new UdpMulticastDiscoveryBearer(opts.Callsign, loggerFactory),
            _ => null,
        };
    }

    /// <summary>
    /// Per-channel beacon emit loop. Each channel keeps its own
    /// "next emit" timestamp so a fast (LAN multicast) and a slow
    /// (HF) channel run on independent cadences from a single timer
    /// without a thread per channel.
    /// </summary>
    private async Task EmitAndSweepAsync(
        IReadOnlyDictionary<string, IDiscoveryBearer> bearers,
        IReadOnlyDictionary<string, IReadOnlyList<DiscoveryChannelInfo>> channelsByBearer,
        CancellationToken stoppingToken)
    {
        var nextEmit = new Dictionary<(string Bearer, string ChannelKey), DateTime>();
        foreach (var kv in channelsByBearer)
        foreach (var ch in kv.Value)
        {
            // Initial fire: a small jitter from now so a freshly-joined
            // node is visible to neighbours within seconds.
            nextEmit[(kv.Key, ch.ChannelKey)] = DateTime.UtcNow.AddMilliseconds(Random.Shared.Next(50, 250));
        }
        var nextSweep = DateTime.UtcNow + SweepInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            foreach (var kv in channelsByBearer)
            {
                var bearer = bearers[kv.Key];
                foreach (var ch in kv.Value)
                {
                    var key = (kv.Key, ch.ChannelKey);
                    if (now < nextEmit[key]) continue;

                    var beacon = new BeaconFrame(
                        Callsign: options.CurrentValue.Callsign,
                        Hops: 0,
                        Ttl: ch.AdvertisedTtlSeconds,
                        // Bearer hint on outgoing beacons is bookkeeping only —
                        // the receiver overrides with its own observation.
                        Bearer: kv.Key == "udp"
                            ? new UdpBearerHint(ch.ChannelKey)
                            : new AgwBearerHint(0));
                    try
                    {
                        await bearer.AnnounceAsync(beacon, ch.ChannelKey, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "DiscoveryService: announce on {0}/{1} failed",
                            kv.Key, ch.ChannelKey);
                    }
                    nextEmit[key] = now.AddSeconds(Math.Max(5, ch.BeaconIntervalSeconds));
                }
            }

            if (now >= nextSweep)
            {
                try
                {
                    var aged = await database.AgeOutDiscoveredPeers(now);
                    if (aged > 0) logger.LogInformation("DiscoveryService: aged out {0} stale peer(s)", aged);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiscoveryService: age-out sweep failed");
                }
                nextSweep = now + SweepInterval;
            }

            // Sleep until the nearest of: next emit, next sweep, or 1s.
            var soonest = nextEmit.Values.Min();
            if (nextSweep < soonest) soonest = nextSweep;
            var sleep = soonest - DateTime.UtcNow;
            if (sleep < TimeSpan.FromMilliseconds(50)) sleep = TimeSpan.FromMilliseconds(50);
            if (sleep > TimeSpan.FromSeconds(1)) sleep = TimeSpan.FromSeconds(1);
            try { await Task.Delay(sleep, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ListenLoopAsync(IDiscoveryBearer bearer, CancellationToken ct)
    {
        try
        {
            await foreach (var received in bearer.ListenAsync(ct).WithCancellation(ct))
            {
                try
                {
                    await UpsertAsync(bearer.Name, received);
                    logger.LogInformation(
                        "DiscoveryService: heard {0} via {1}/{2} (hops={3}, advertised-ttl={4}s)",
                        received.Beacon.Callsign, bearer.Name, received.ChannelKey,
                        received.Beacon.Hops, received.Beacon.Ttl);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiscoveryService: upsert failed for {0}", received.Beacon.Callsign);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task UpsertAsync(string bearerName, ReceivedBeacon received)
    {
        // Look up the channel row to denormalize LinkClass + CostHint
        // onto the discovered-peer record. The row should always exist
        // (we only listen on configured channels) but be defensive.
        var allChannels = await database.GetDiscoveryChannels();
        var channel = allChannels.FirstOrDefault(c =>
            string.Equals(c.Bearer, bearerName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.ChannelKey, received.ChannelKey, StringComparison.Ordinal));

        var beacon = received.Beacon;
        var row = new DbDiscoveredPeer
        {
            Callsign = beacon.Callsign,
            Bearer = bearerName,
            ChannelId = channel?.Id ?? 0,
            ChannelKey = received.ChannelKey,
            LinkClass = channel?.LinkClass ?? LinkClass.Unknown,
            CostHint = channel?.CostHint ?? LinkClassDefaults.CostHint(LinkClass.Unknown),
            Hops = beacon.Hops,
            TtlSeconds = beacon.Ttl,
            BpqPort = beacon.Bearer is AgwBearerHint a ? a.BpqPort : null,
            UdpEndpoint = beacon.Bearer is UdpBearerHint u ? u.Endpoint : null,
            LastSeen = DateTime.UtcNow,
        };
        await database.UpsertDiscoveredPeer(row);
    }
}
