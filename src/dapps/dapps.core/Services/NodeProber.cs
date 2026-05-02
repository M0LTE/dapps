using dapps.client;
using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.core.Services;

/// <summary>
/// Plan B6.1 — connected-mode probe-and-map. Performs a single
/// reachability probe: open an AGW session to <c>(callsign, port)</c>,
/// look for a <c>DAPPSv1&gt;</c> banner, then disconnect cleanly. The
/// scheduler ( <see cref="ProbeSchedulerService"/> ) and the on-demand
/// REST endpoint both call into this — schedule decisions and result
/// persistence live elsewhere.
///
/// The class is deliberately stateless (per-call Connect → Read prompt
/// → Disconnect) so the same instance can serve concurrent probes; the
/// underlying <see cref="IDappsOutboundTransport"/> opens a fresh TCP
/// socket per probe, just as the forwarder does.
/// </summary>
public sealed class NodeProber(
    IDappsOutboundTransport transport,
    ILoggerFactory loggerFactory,
    ILogger<NodeProber> logger)
{
    /// <summary>Outcome of a single probe attempt. <see cref="Success"/>
    /// is true iff the prompt was observed end-to-end. <see cref="Error"/>
    /// is empty on success, populated with a short human-readable
    /// description on failure (so the dashboard can surface it).
    /// <see cref="DiscoveredPeers"/> is populated when the caller passed
    /// <c>fetchPeers: true</c> and the remote answered the
    /// <c>peers</c> query — Plan B6.1 Phase 2 transitive discovery.
    /// Empty when peers wasn't requested or the request failed; failure
    /// to fetch peers does not mark the probe itself as failed.</summary>
    public sealed record ProbeResult(
        string Callsign,
        int BpqPort,
        bool Success,
        string Error,
        DateTime At,
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> DiscoveredPeers);

    /// <summary>
    /// Connect to <paramref name="remoteCallsign"/> on
    /// <paramref name="bpqPort"/>, look for the <c>DAPPSv1&gt;</c>
    /// banner, optionally ask the remote for its peers (Plan B6.1
    /// Phase 2), then disconnect. Captures the most common AGW
    /// failure modes (no socket, AGW reject, no prompt) into a stable
    /// <see cref="ProbeResult.Error"/> string rather than throwing.
    ///
    /// When <paramref name="fetchPeers"/> is true, a successful probe
    /// continues with a <c>peers</c> query whose result is reported
    /// in <see cref="ProbeResult.DiscoveredPeers"/>. A failed peers
    /// query is logged but does not flip the probe to failed — the
    /// remote *did* answer DAPPSv1&gt; and that's still useful state.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        string localCallsign,
        string remoteCallsign,
        int bpqPort,
        CancellationToken ct,
        bool fetchPeers = false)
    {
        var at = DateTime.UtcNow;
        IReadOnlyList<DappsProtocolClient.DiscoveredPeerInfo> peers = [];
        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: remoteCallsign,
                bpqPortNumber: bpqPort,
                stoppingToken: ct);

            var protocol = new DappsProtocolClient(connection.Stream, loggerFactory);

            if (!await protocol.ReadInitialPromptAsync(ct))
            {
                return new ProbeResult(remoteCallsign, bpqPort, false,
                    "no DAPPSv1> prompt", at, peers);
            }

            if (fetchPeers)
            {
                try
                {
                    peers = await protocol.RequestPeersAsync(ct);
                    logger.LogInformation("Probe ok: {0} on port {1} (got {2} peer record(s))",
                        remoteCallsign, bpqPort, peers.Count);
                }
                catch (Exception ex)
                {
                    logger.LogInformation(
                        "Probe ok but peers query failed: {0} on port {1} ({2})",
                        remoteCallsign, bpqPort, ex.Message);
                }
            }
            else
            {
                logger.LogInformation("Probe ok: {0} on port {1}", remoteCallsign, bpqPort);
            }
            return new ProbeResult(remoteCallsign, bpqPort, true, "", at, peers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation — re-throw so the scheduler can
            // exit cleanly on shutdown.
            throw;
        }
        catch (TimeoutException ex)
        {
            logger.LogInformation("Probe timeout: {0} ({1})", remoteCallsign, ex.Message);
            return new ProbeResult(remoteCallsign, bpqPort, false, $"timeout: {ex.Message}", at, peers);
        }
        catch (Exception ex)
        {
            // Probes are best-effort; a connect failure is the answer,
            // not an exception to bubble. Log at info — failures are
            // expected and not actionable noise.
            logger.LogInformation("Probe failed: {0} ({1})", remoteCallsign, ex.Message);
            return new ProbeResult(remoteCallsign, bpqPort, false, ex.Message, at, peers);
        }
    }
}
