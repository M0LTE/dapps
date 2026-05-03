namespace dapps.core.Services;

/// <summary>
/// Background loop that pokes <see cref="OutboundMessageManager.DoRun"/>
/// on a short tick. Without this, messages submitted to the queue just
/// sit there until an operator calls <c>POST /Message/dorun</c> by
/// hand - which is fine for a CLI test fixture but no good for an
/// actually-deployed node.
///
/// Tick is fixed at 5 seconds: short enough that an interactive
/// operator pressing "send" sees their message move within a beat,
/// long enough to cost negligible CPU on an idle node. The forwarder's
/// own work is bounded by the queue size + the bearer's per-message
/// latency, so a long-running tick can overlap the next tick - the
/// <see cref="OutboundMessageManager.DoRun"/> mutex handles that
/// (concurrent calls return immediately).
///
/// Manual <c>POST /Message/dorun</c> still works alongside this - it
/// goes through the same mutex, so two pokes won't double-process the
/// queue. Useful for "kick now" semantics when an operator wants the
/// next tick to happen *now*.
/// </summary>
public sealed class OutboundForwarderService(
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<OutboundForwarderService> logger) : BackgroundService
{
    /// <summary>Cadence between forwarder runs. Tunable in tests via
    /// init-only setters; production defaults to 5 s.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Brief delay before the first run so the rest of the
    /// service surface (DB schema creation, MQTT broker bind, AGW
    /// connect, discovery channel join) gets a chance to settle.
    /// Picking the first message off the queue before AGW is reachable
    /// would just log a forwarding failure and re-queue.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Resolve OutboundMessageManager lazily on first tick rather
        // than via the ctor - IDappsOutboundTransport's factory reads
        // SystemOptions at construction, which is fine post-build but
        // gratuitous to chain through during DI graph materialisation.
        var outbound = services.GetRequiredService<OutboundMessageManager>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await outbound.DoRun(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A bearer-level fault on a single message is already
                // caught inside DoRun; anything that escapes is the
                // queue-iteration logic itself. Log and try again next
                // tick rather than tearing the whole hosted service
                // down - a stuck forwarder is worse than a noisy one.
                logger.LogError(ex, "Forwarder tick failed; will retry on next interval");
            }

            try { await Task.Delay(TickInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
