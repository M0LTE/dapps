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
    /// description on failure (so the dashboard can surface it).</summary>
    public sealed record ProbeResult(
        string Callsign,
        int BpqPort,
        bool Success,
        string Error,
        DateTime At);

    /// <summary>
    /// Connect to <paramref name="remoteCallsign"/> on
    /// <paramref name="bpqPort"/>, look for the <c>DAPPSv1&gt;</c>
    /// banner, then disconnect. Captures the most common AGW failure
    /// modes (no socket, AGW reject, no prompt) into a stable
    /// <see cref="ProbeResult.Error"/> string rather than throwing.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        string localCallsign,
        string remoteCallsign,
        int bpqPort,
        CancellationToken ct)
    {
        var at = DateTime.UtcNow;
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
                    "no DAPPSv1> prompt", at);
            }

            logger.LogInformation("Probe ok: {0} on port {1}", remoteCallsign, bpqPort);
            return new ProbeResult(remoteCallsign, bpqPort, true, "", at);
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
            return new ProbeResult(remoteCallsign, bpqPort, false, $"timeout: {ex.Message}", at);
        }
        catch (Exception ex)
        {
            // Probes are best-effort; a connect failure is the answer,
            // not an exception to bubble. Log at info — failures are
            // expected and not actionable noise.
            logger.LogInformation("Probe failed: {0} ({1})", remoteCallsign, ex.Message);
            return new ProbeResult(remoteCallsign, bpqPort, false, ex.Message, at);
        }
    }
}
