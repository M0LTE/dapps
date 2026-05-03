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
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<DiscoveryService> logger,
    AirtimeAccountant? airtime = null,
    OperationalMetrics? metrics = null) : BackgroundService
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
            try { await Task.Delay(StartupPollInterval, timeProvider, stoppingToken); }
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
                r.BeaconIntervalSeconds, r.AdvertisedTtlSeconds, r.CostHint,
                r.AirtimeBudgetSecondsPerHour, r.SolicitIntervalSeconds)).ToList();
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

        // Publish the running bearers so the on-demand SolicitAsync
        // entry point can find them. Cleared in the finally below so
        // a service-stop doesn't leave dangling references.
        lock (_activeBearers)
        {
            _activeBearers.Clear();
            foreach (var kv in bearers) _activeBearers[kv.Key] = kv.Value;
        }

        var listenTasks = bearers.Select(kv =>
            Task.Run(() => ListenLoopAsync(kv.Value, channelsByBearer[kv.Key], stoppingToken), stoppingToken)).ToList();

        try
        {
            await EmitAndSweepAsync(bearers, channelsByBearer, stoppingToken);
        }
        finally
        {
            try { await Task.WhenAll(listenTasks); } catch { /* expected on shutdown */ }
            lock (_activeBearers) _activeBearers.Clear();
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
        // Plan B6.2 follow-up — per-channel scheduled solicit. Only
        // populated for channels with a positive SolicitIntervalSeconds.
        // The first solicit fires one full interval after start (not
        // immediately) — beacons cover the freshly-joined-node case
        // already, and an immediate solicit would step on our own
        // initial beacon.
        var nextSolicit = new Dictionary<(string Bearer, string ChannelKey), DateTime>();
        foreach (var kv in channelsByBearer)
        foreach (var ch in kv.Value)
        {
            // Initial fire: a small jitter from now so a freshly-joined
            // node is visible to neighbours within seconds.
            nextEmit[(kv.Key, ch.ChannelKey)] = timeProvider.GetUtcNow().UtcDateTime.AddMilliseconds(Random.Shared.Next(50, 250));
            if (ch.SolicitIntervalSeconds > 0)
            {
                nextSolicit[(kv.Key, ch.ChannelKey)] =
                    timeProvider.GetUtcNow().UtcDateTime.AddSeconds(ch.SolicitIntervalSeconds);
            }
        }
        var nextSweep = timeProvider.GetUtcNow().UtcDateTime + SweepInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            foreach (var kv in channelsByBearer)
            {
                var bearer = bearers[kv.Key];
                foreach (var ch in kv.Value)
                {
                    var key = (kv.Key, ch.ChannelKey);
                    if (now < nextEmit[key]) continue;

                    // Plan B7 — airtime budget. Defer this beacon if the
                    // operator-set cap would be blown; reschedule a short
                    // way out so we keep checking each tick. Without the
                    // accountant (DI hasn't supplied one in tests, or
                    // budget is 0) TryReserve always returns true.
                    var beaconCost = LinkClassDefaults.AirtimeSecondsEstimate(ch.LinkClass, AirtimeKind.Beacon);
                    if (airtime is { } acct && !acct.TryReserve(beaconCost, $"beacon {kv.Key}/{ch.ChannelKey}", ch.ChannelKey, ch.AirtimeBudgetSecondsPerHour))
                    {
                        // Try again in a quarter of the regular interval —
                        // budgets free up as old entries roll out of the
                        // 60-min window, so we don't want to block this
                        // channel for the full beacon interval.
                        var deferSec = Math.Max(15, Math.Max(5, ch.BeaconIntervalSeconds) / 4);
                        nextEmit[key] = now.AddSeconds(deferSec);
                        continue;
                    }

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

                // Plan B6.2 — scheduled solicit cadence. Independent of
                // beacons: a channel may want to beacon every 30 min and
                // solicit every 4 h, or beacon never (Enabled but
                // BeaconIntervalSeconds large) and solicit on a tighter
                // schedule. Both still gate through the airtime budget.
                foreach (var ch in kv.Value)
                {
                    var key = (kv.Key, ch.ChannelKey);
                    if (!nextSolicit.TryGetValue(key, out var solicitDue)) continue;
                    if (now < solicitDue) continue;

                    var solicitCost = LinkClassDefaults.AirtimeSecondsEstimate(ch.LinkClass, AirtimeKind.Solicit);
                    if (airtime is { } sacct && !sacct.TryReserve(solicitCost, $"scheduled-solicit {kv.Key}/{ch.ChannelKey}", ch.ChannelKey, ch.AirtimeBudgetSecondsPerHour))
                    {
                        // Same defer math as scheduled beacons — a
                        // quarter of the regular interval keeps us
                        // checking but doesn't burn cycles re-trying.
                        var deferSec = Math.Max(15, ch.SolicitIntervalSeconds / 4);
                        nextSolicit[key] = now.AddSeconds(deferSec);
                        continue;
                    }

                    var solicit = new SolicitFrame(options.CurrentValue.Callsign);
                    try
                    {
                        await bearer.SolicitAsync(solicit, ch.ChannelKey, stoppingToken);
                        logger.LogInformation(
                            "DiscoveryService: scheduled solicit on {0}/{1}",
                            kv.Key, ch.ChannelKey);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "DiscoveryService: scheduled solicit on {0}/{1} failed",
                            kv.Key, ch.ChannelKey);
                    }
                    nextSolicit[key] = now.AddSeconds(Math.Max(15, ch.SolicitIntervalSeconds));
                }
            }

            if (now >= nextSweep)
            {
                try
                {
                    var aged = await database.AgeOutDiscoveredPeers(now);
                    if (aged.Count > 0)
                    {
                        logger.LogInformation("DiscoveryService: aged out {0} stale peer(s)", aged.Count);
                        foreach (var p in aged)
                        {
                            metrics?.RecordPeerAgedOut(p.Callsign, p.Bearer, p.ChannelKey);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiscoveryService: age-out sweep failed");
                }
                nextSweep = now + SweepInterval;
            }

            // Sleep until the nearest of: next emit, next solicit,
            // next sweep, or 1s.
            var soonest = nextEmit.Values.Min();
            if (nextSolicit.Count > 0)
            {
                var soonestSolicit = nextSolicit.Values.Min();
                if (soonestSolicit < soonest) soonest = soonestSolicit;
            }
            if (nextSweep < soonest) soonest = nextSweep;
            var sleep = soonest - timeProvider.GetUtcNow().UtcDateTime;
            if (sleep < TimeSpan.FromMilliseconds(50)) sleep = TimeSpan.FromMilliseconds(50);
            if (sleep > TimeSpan.FromSeconds(1)) sleep = TimeSpan.FromSeconds(1);
            try { await Task.Delay(sleep, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Plan B6.2 — bound on the random delay before responding to an
    /// incoming solicit. Each receiver picks a uniform random duration
    /// in [0, this] before emitting its beacon, so ten nodes hearing
    /// the same solicit don't all reply at once and saturate the
    /// channel. Tunable so tests can shrink it.
    /// </summary>
    public TimeSpan SolicitResponseMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    private async Task ListenLoopAsync(
        IDiscoveryBearer bearer,
        IReadOnlyList<DiscoveryChannelInfo> channels,
        CancellationToken ct)
    {
        try
        {
            await foreach (var received in bearer.ListenAsync(ct).WithCancellation(ct))
            {
                try
                {
                    switch (received)
                    {
                        case ReceivedBeacon rb:
                            await UpsertAsync(bearer.Name, rb);
                            logger.LogInformation(
                                "DiscoveryService: heard {0} via {1}/{2} (hops={3}, advertised-ttl={4}s)",
                                rb.Beacon.Callsign, bearer.Name, rb.ChannelKey,
                                rb.Beacon.Hops, rb.Beacon.Ttl);
                            break;

                        case ReceivedSolicit rs:
                            logger.LogInformation(
                                "DiscoveryService: solicit from {0} on {1}/{2} — scheduling reply",
                                rs.Solicit.Callsign, bearer.Name, rs.ChannelKey);
                            // Fire-and-forget: respond on a delay so the
                            // listen loop keeps draining new frames.
                            // Concurrent solicits each schedule their own
                            // reply task; the channel naturally serialises
                            // the actual writes via the bearer's framing.
                            _ = RespondToSolicitAsync(bearer, channels, rs, ct);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DiscoveryService: failed to handle {0}", received.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    /// <summary>
    /// Plan B6.2 — schedule a beacon emission in response to a solicit.
    /// Random 0..<see cref="SolicitResponseMaxDelay"/> jitter avoids the
    /// "ten nodes hear the same solicit, all reply at once" channel-
    /// saturation case. Skips silently if we don't have the channel
    /// configured (we shouldn't be receiving on a channel we didn't
    /// configure, but be defensive).
    /// </summary>
    private async Task RespondToSolicitAsync(
        IDiscoveryBearer bearer,
        IReadOnlyList<DiscoveryChannelInfo> channels,
        ReceivedSolicit rs,
        CancellationToken ct)
    {
        var ch = channels.FirstOrDefault(c =>
            string.Equals(c.ChannelKey, rs.ChannelKey, StringComparison.Ordinal));
        if (ch is null) return;

        var maxMs = (int)Math.Max(0, SolicitResponseMaxDelay.TotalMilliseconds);
        var delayMs = maxMs > 0 ? Random.Shared.Next(0, maxMs) : 0;

        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), timeProvider, ct);
            }
            // Plan B7 — solicit replies count against the same budget as
            // scheduled beacons. Skip the reply if the budget says no;
            // we'll catch the soliciting peer on the next regular beacon.
            var solicitCost = LinkClassDefaults.AirtimeSecondsEstimate(ch.LinkClass, AirtimeKind.Solicit);
            if (airtime is { } acct && !acct.TryReserve(solicitCost, $"solicit-reply {bearer.Name}/{ch.ChannelKey}", ch.ChannelKey, ch.AirtimeBudgetSecondsPerHour))
            {
                logger.LogInformation(
                    "DiscoveryService: budget exhausted, NOT replying to solicit from {0} on {1}/{2}",
                    rs.Solicit.Callsign, bearer.Name, rs.ChannelKey);
                return;
            }
            var beacon = new BeaconFrame(
                Callsign: options.CurrentValue.Callsign,
                Hops: 0,
                Ttl: ch.AdvertisedTtlSeconds,
                Bearer: bearer.Name == "udp"
                    ? new UdpBearerHint(ch.ChannelKey)
                    : new AgwBearerHint(0));
            await bearer.AnnounceAsync(beacon, ch.ChannelKey, ct);
            logger.LogInformation(
                "DiscoveryService: replied to solicit from {0} on {1}/{2} after {3}ms",
                rs.Solicit.Callsign, bearer.Name, rs.ChannelKey, delayMs);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "DiscoveryService: failed to reply to solicit on {0}/{1}",
                bearer.Name, rs.ChannelKey);
        }
    }

    /// <summary>
    /// Public entry point used by the controller to fire a one-shot
    /// solicit. Bearer/channel must be currently configured and
    /// running. Plan B6.2.
    /// </summary>
    public async Task SolicitAsync(string bearerName, string channelKey, CancellationToken ct)
    {
        IDiscoveryBearer? bearer;
        lock (_activeBearers)
        {
            _activeBearers.TryGetValue(bearerName, out bearer);
        }
        if (bearer is null)
        {
            throw new InvalidOperationException(
                $"No active discovery bearer named '{bearerName}'");
        }
        var solicit = new SolicitFrame(options.CurrentValue.Callsign);
        await bearer.SolicitAsync(solicit, channelKey, ct);
        logger.LogInformation(
            "DiscoveryService: emitted solicit on {0}/{1}", bearerName, channelKey);
    }

    /// <summary>Bearers currently running; populated in
    /// <see cref="ExecuteAsync"/> after start, cleared on shutdown.
    /// Surfaces the on-demand <see cref="SolicitAsync"/> path without
    /// a circular DI dependency between the service and the controller.</summary>
    private readonly Dictionary<string, IDiscoveryBearer> _activeBearers = new(StringComparer.Ordinal);

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
            LastSeen = timeProvider.GetUtcNow().UtcDateTime,
        };
        await database.UpsertDiscoveredPeer(row);

        // Plan B6.1 Phase 2b — auto-seed node-prompt candidates from
        // AGW beacons. Derive the BASE callsign of the beacon source
        // (the bit before the SSID hyphen) and register it as a probe
        // target whose Source flag tells the scheduler to use the
        // node-prompt navigation path. UDP beacons are skipped — we
        // can't reach the NODECALL via UDP, only via AGW.
        if (options.CurrentValue.AutoDiscoverViaNodeCall
            && beacon.Bearer is AgwBearerHint agw)
        {
            await SeedNodePromptCandidateAsync(beacon.Callsign, agw.BpqPort);
        }
    }

    private async Task SeedNodePromptCandidateAsync(string sourceCallsign, int bpqPort)
    {
        var baseCallsign = sourceCallsign.Split('-')[0].ToUpperInvariant();
        var ourBase = options.CurrentValue.Callsign.Split('-')[0];
        if (string.Equals(baseCallsign, ourBase, StringComparison.OrdinalIgnoreCase))
        {
            // Don't seed candidates for ourselves — we'd be probing
            // our own NODECALL, which doesn't loop through L2.
            return;
        }

        // Skip if we already track ANY probe state for this base callsign
        // — direct, transitive, or a previous node-prompt seed. The
        // existing row's Source is more authoritative than an
        // auto-seeded one, so don't clobber.
        var existing = await database.GetProbedNode(baseCallsign);
        if (existing is not null) return;

        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = baseCallsign,
            LastBpqPort = bpqPort,
            Source = $"node-prompt:{sourceCallsign}",
        });
        logger.LogInformation(
            "Auto-discovered node-prompt candidate: {0} (derived from beacon {1} on AGW port {2})",
            baseCallsign, sourceCallsign, bpqPort);
    }
}
