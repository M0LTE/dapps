using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.core.Services;

/// <summary>
/// Plan F3b - single-shot rev poll over an existing AGW transport.
/// Opens a fresh session to the target, waits for the
/// <c>DAPPSv1&gt;</c> banner, sends <c>rev</c>, drains every offered
/// message through <see cref="IBackhaulInbox.DeliverAsync"/>, and
/// disconnects. Stateless - the same instance can serve many
/// concurrent polls.
///
/// Mirror of <see cref="NodeProber"/> for the C5.1-style reachability
/// case; the difference is that this one actually drains the
/// remote's queued mail, where the prober just confirms the session
/// reaches the prompt.
/// </summary>
public sealed class NodePoller(
    IDappsOutboundTransport transport,
    IBackhaulInbox inbox,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<NodePoller> logger)
{
    /// <summary>Outcome of a single poll. Failure is captured rather
    /// than thrown - the scheduler catches per-callsign failures so
    /// one unreachable neighbour doesn't tank the whole sweep.</summary>
    public sealed record PollResult(
        string Callsign,
        bool Success,
        int MessagesDrained,
        string Error,
        DateTime At);

    public async Task<PollResult> PollAsync(
        string localCallsign,
        string remoteCallsign,
        int bpqPort,
        CancellationToken ct)
    {
        var at = timeProvider.GetUtcNow().UtcDateTime;
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
                return new PollResult(remoteCallsign, false, 0, "no DAPPSv1> prompt", at);
            }

            var drained = 0;
            await foreach (var polled in protocol.PollAsync(requestedIds: null, ct))
            {
                var inbound = new BackhaulMessage(
                    Id: polled.Id,
                    Destination: polled.Destination,
                    Salt: polled.Salt,
                    Ttl: polled.Ttl,
                    Payload: polled.Payload,
                    Originator: polled.Originator,
                    MasterId: polled.MasterId,
                    FragmentIndex: polled.FragmentIndex,
                    FragmentTotal: polled.FragmentTotal);
                await inbox.DeliverAsync(inbound, remoteCallsign, ct);
                drained++;
            }

            logger.LogInformation("Poll ok: {0} drained {1} message(s)", remoteCallsign, drained);
            return new PollResult(remoteCallsign, true, drained, "", at);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation - re-throw so the scheduler
            // can exit cleanly on shutdown.
            throw;
        }
        catch (TimeoutException ex)
        {
            logger.LogInformation("Poll timeout: {0} ({1})", remoteCallsign, ex.Message);
            return new PollResult(remoteCallsign, false, 0, $"timeout: {ex.Message}", at);
        }
        catch (Exception ex)
        {
            logger.LogInformation("Poll failed: {0} ({1})", remoteCallsign, ex.Message);
            return new PollResult(remoteCallsign, false, 0, ex.Message, at);
        }
    }
}
