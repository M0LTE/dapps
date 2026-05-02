using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan B6.1 — connected-mode probe-and-map scheduler. When
/// <see cref="SystemOptions.ProbingEnabled"/> is true, runs a slow
/// sweep of every known peer (manual <see cref="DbNeighbour"/> +
/// AGW-bearer <see cref="DbDiscoveredPeer"/>, less opt-outs) and
/// records reachability into <see cref="DbProbedNode"/>.
///
/// Cadence: a startup grace, then a sweep every
/// <see cref="SystemOptions.ProbeIntervalHours"/> hours. Within a
/// sweep, individual probes are spaced by a small random jitter so
/// we don't burst every BPQ on the network simultaneously. Off-by-
/// default — sysops opt in.
///
/// This class is the schedule + state-update half of B6.1; the actual
/// probe transaction lives in <see cref="NodeProber"/>. Operator-
/// triggered probes from the REST surface use the same prober but
/// bypass this scheduler.
/// </summary>
public sealed class ProbeSchedulerService(
    NodeProber prober,
    Database database,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<ProbeSchedulerService> logger) : BackgroundService
{
    /// <summary>Delay before the first sweep after startup. Long enough
    /// for AGW reconnect, MQTT broker init, and the first beacon round
    /// to land — probing into a node that just booted produces noise
    /// rather than signal.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Bounds on the per-probe jitter. Picks a uniform random
    /// duration inside this window between consecutive probes within a
    /// single sweep, so two nodes running on the same cron offset don't
    /// dial the same BPQ at the same instant.</summary>
    public TimeSpan MinInterProbeDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxInterProbeDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often to re-check whether probing has been enabled
    /// while the service has been idling. Probing toggles via /Config
    /// without a restart, so we can't sleep through the
    /// disabled-then-enabled transition.</summary>
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup grace. Tests use a tiny StartupDelay; production
        // defaults to 15 minutes so other hosted services have a
        // chance to settle.
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            if (!opts.ProbingEnabled)
            {
                try { await Task.Delay(DisabledPollInterval, timeProvider, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await SweepAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Probe sweep failed");
            }

            var hours = Math.Max(1, opts.ProbeIntervalHours);
            try { await Task.Delay(TimeSpan.FromHours(hours), timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Walk every eligible probe target once, sequentially with
    /// jitter between probes. Public so a triggered "sweep now" path
    /// can call into the same machinery; the scheduler loop calls it
    /// internally on its cadence.</summary>
    public async Task SweepAsync(SystemOptions opts, CancellationToken ct)
    {
        var targets = await EnumerateTargets(opts);
        if (targets.Count == 0)
        {
            logger.LogDebug("Probe sweep: no eligible targets");
            return;
        }

        logger.LogInformation("Probe sweep: {0} target(s)", targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = targets[i];
            await ProbeAndRecordAsync(opts.Callsign, t.Callsign, t.BpqPort, ct);
            if (i < targets.Count - 1)
            {
                var jitter = RandomDelayMs(MinInterProbeDelay, MaxInterProbeDelay);
                try { await Task.Delay(TimeSpan.FromMilliseconds(jitter), timeProvider, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Run a single probe and persist the outcome into
    /// <see cref="DbProbedNode"/>. Used by the scheduler and by the
    /// on-demand REST endpoint. Returns the row as written.
    /// <paramref name="fetchPeers"/> controls Plan B6.1 Phase 2
    /// transitive discovery — when true, a successful probe asks the
    /// remote for its peers and stores any new callsigns as candidate
    /// rows for future sweeps. The default is true; opt out by
    /// passing false (e.g. in unit tests that don't model the response).
    /// </summary>
    public async Task<DbProbedNode> ProbeAndRecordAsync(
        string localCallsign, string remoteCallsign, int bpqPort, CancellationToken ct,
        bool fetchPeers = true)
    {
        var result = await prober.ProbeAsync(localCallsign, remoteCallsign, bpqPort, ct, fetchPeers);
        var row = await RecordResultAsync(result);
        if (result.Success && result.DiscoveredPeers.Count > 0)
        {
            await PersistTransitiveDiscoveriesAsync(result);
        }
        return row;
    }

    /// <summary>Apply a probe result to the persisted row (insert if
    /// missing, update otherwise). Preserves <see cref="DbProbedNode.OptOut"/>
    /// across updates and bumps <c>ConsecutiveFailures</c> /
    /// <c>SuccessCount</c>. Sets <see cref="DbProbedNode.Source"/> on
    /// first insert (defaulting to <c>neighbour</c>); never overwrites
    /// it on update — the row's origin is a fact about how we first
    /// heard about the callsign and shouldn't drift over time.</summary>
    public async Task<DbProbedNode> RecordResultAsync(NodeProber.ProbeResult result)
    {
        var existing = await database.GetProbedNode(result.Callsign);
        var row = existing ?? new DbProbedNode
        {
            Callsign = result.Callsign,
            // First time we've ever recorded this callsign — assume
            // it's a neighbour-class probe target. Transitively-
            // discovered candidates set their own Source explicitly
            // before the first probe runs (see PersistTransitiveDiscoveriesAsync).
            Source = "neighbour",
        };
        row.LastBpqPort = result.BpqPort;
        row.LastProbedAt = result.At;
        if (result.Success)
        {
            row.LastSuccessAt = result.At;
            row.LastError = "";
            row.ConsecutiveFailures = 0;
            row.SuccessCount = unchecked(row.SuccessCount + 1);
        }
        else
        {
            row.LastError = result.Error;
            row.ConsecutiveFailures = unchecked(row.ConsecutiveFailures + 1);
        }
        await database.UpsertProbedNode(row);
        return row;
    }

    /// <summary>
    /// Plan B6.1 Phase 2 — record transitively-discovered callsigns as
    /// candidate <see cref="DbProbedNode"/> rows. Skips callsigns we
    /// already track (we don't want hearsay overwriting fresher first-
    /// hand probe state) and skips ourselves (the remote always reports
    /// us as a peer, since we just talked to them — recording that is
    /// noise). Records the asking peer's callsign in <c>Source</c> so a
    /// sysop can tell where each candidate came from. Best guess for
    /// the port is the same port we just probed the source peer on —
    /// usually right when the network is one shared frequency, often
    /// wrong otherwise; the next sweep surfaces the mismatch as a
    /// regular probe failure.
    /// </summary>
    private async Task PersistTransitiveDiscoveriesAsync(NodeProber.ProbeResult result)
    {
        var ourCallsign = options.CurrentValue.Callsign;
        foreach (var p in result.DiscoveredPeers)
        {
            if (string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (string.Equals(p.Callsign, ourCallsign, StringComparison.OrdinalIgnoreCase)) continue;

            var existing = await database.GetProbedNode(p.Callsign);
            if (existing is not null) continue;   // we already track this — don't clobber direct state

            await database.UpsertProbedNode(new DbProbedNode
            {
                Callsign = p.Callsign.ToUpperInvariant(),
                LastBpqPort = p.BpqPort ?? result.BpqPort,
                Source = $"via:{result.Callsign}",
                // No probe attempt yet — leave LastProbedAt / LastSuccessAt
                // null; the scheduler picks it up on the next sweep.
            });
            logger.LogInformation(
                "Transitive discovery: {0} via {1} (port {2})",
                p.Callsign, result.Callsign, p.BpqPort ?? result.BpqPort);
        }
    }

    /// <summary>
    /// Build the eligible target list for a sweep. Sources:
    /// <see cref="DbNeighbour"/> rows without a UDP endpoint (AGW-routable),
    /// AGW-bearer <see cref="DbDiscoveredPeer"/> rows, and Phase-2
    /// transitive candidates — <see cref="DbProbedNode"/> rows whose
    /// <c>Source</c> starts with <c>via:</c> (i.e. learned from another
    /// peer's <c>peers</c> response, not yet on either of the other
    /// two surfaces). Per-callsign opt-outs from
    /// <see cref="DbProbedNode.OptOut"/> are honoured. Port preference
    /// (highest precedence first): neighbour's <c>BpqPort</c>, peer's
    /// observed <c>BpqPort</c>, candidate's stored <c>LastBpqPort</c>,
    /// <see cref="SystemOptions.DefaultBpqPort"/>.
    /// </summary>
    public async Task<IReadOnlyList<ProbeTarget>> EnumerateTargets(SystemOptions opts)
    {
        var neighbours = await database.GetNeighbours();
        var peers = await database.GetDiscoveredPeers();
        var probed = await database.GetProbedNodes();

        var optOut = probed
            .Where(p => p.OptOut)
            .Select(p => p.Callsign)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = new Dictionary<string, ProbeTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in neighbours)
        {
            if (string.IsNullOrWhiteSpace(n.Callsign)) continue;
            if (n.UdpEndpoint is not null) continue;   // UDP path — no AGW probe possible
            if (optOut.Contains(n.Callsign)) continue;
            targets[n.Callsign] = new ProbeTarget(n.Callsign, n.BpqPort ?? opts.DefaultBpqPort);
        }

        foreach (var p in peers)
        {
            if (!string.Equals(p.Bearer, "agw", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(p.Callsign)) continue;
            if (optOut.Contains(p.Callsign)) continue;
            // Don't overwrite a manual neighbour's port with a guessed one.
            if (targets.ContainsKey(p.Callsign)) continue;
            targets[p.Callsign] = new ProbeTarget(p.Callsign, p.BpqPort ?? opts.DefaultBpqPort);
        }

        // Phase 2 transitive candidates — DbProbedNode rows whose Source
        // says they were learned via someone else's peers list. Without
        // this, a transitively-discovered callsign would sit in the
        // table forever waiting for an operator to manually probe it.
        foreach (var c in probed)
        {
            if (c.OptOut) continue;
            if (string.IsNullOrWhiteSpace(c.Callsign)) continue;
            if (!c.Source.StartsWith("via:", StringComparison.OrdinalIgnoreCase)) continue;
            if (targets.ContainsKey(c.Callsign)) continue;
            targets[c.Callsign] = new ProbeTarget(c.Callsign, c.LastBpqPort ?? opts.DefaultBpqPort);
        }

        return targets.Values
            .OrderBy(t => t.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RandomDelayMs(TimeSpan min, TimeSpan max)
    {
        var lo = (int)Math.Max(0, min.TotalMilliseconds);
        var hi = (int)Math.Max(lo + 1, max.TotalMilliseconds);
        return Random.Shared.Next(lo, hi);
    }

    public sealed record ProbeTarget(string Callsign, int BpqPort);
}
