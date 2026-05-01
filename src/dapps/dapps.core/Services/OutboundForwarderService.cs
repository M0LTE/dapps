namespace dapps.core.Services;

/// <summary>
/// Background loop that pokes <see cref="OutboundMessageManager.DoRun"/>
/// on a short tick. Without this, messages submitted to the queue just
/// sit there until an operator calls <c>POST /Message/dorun</c> by
/// hand — which is fine for a CLI test fixture but no good for an
/// actually-deployed node.
///
/// Tick is fixed at 5 seconds: short enough that an interactive
/// operator pressing "send" sees their message move within a beat,
/// long enough to cost negligible CPU on an idle node. The forwarder's
/// own work is bounded by the queue size + the bearer's per-message
/// latency, so a long-running tick can overlap the next tick — the
/// <see cref="OutboundMessageManager.DoRun"/> mutex handles that
/// (concurrent calls return immediately).
///
/// Manual <c>POST /Message/dorun</c> still works alongside this — it
/// goes through the same mutex, so two pokes won't double-process the
/// queue. Useful for "kick now" semantics when an operator wants the
/// next tick to happen *now*.
/// </summary>
public sealed class OutboundForwarderService : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly ILogger<OutboundForwarderService> logger;
    private readonly TimeSpan tickInterval;
    private readonly TimeSpan startupDelay;

    public OutboundForwarderService(
        IServiceProvider services,
        ILogger<OutboundForwarderService> logger)
        : this(services, logger, TickInterval, StartupDelay) { }

    /// <summary>Test-only ctor: shorten the timings so unit tests don't have
    /// to sit through the production-defaults 3s + 5s intervals.</summary>
    internal OutboundForwarderService(
        IServiceProvider services,
        ILogger<OutboundForwarderService> logger,
        TimeSpan tickInterval,
        TimeSpan startupDelay)
    {
        this.services = services;
        this.logger = logger;
        this.tickInterval = tickInterval;
        this.startupDelay = startupDelay;
    }

    // Resolve OutboundMessageManager lazily on first tick, NOT in
    // the ctor. Eager construction would chain through to the
    // IDappsOutboundTransport factory, which reads SystemOptions
    // at construction — that needs DbStartup to have finished
    // first, and DbStartup hasn't been run when hosted-service ctors
    // fire during DI resolution.
    /// <summary>Cadence between forwarder runs.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Brief delay before the first run so the rest of the service
    /// surface (DB schema creation, MQTT broker bind, AGW connect,
    /// discovery channel join) gets a chance to settle. Picking the
    /// first message off the queue before AGW is reachable would just
    /// log a forwarding failure and re-queue.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(startupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

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
                // down — a stuck forwarder is worse than a noisy one.
                logger.LogError(ex, "Forwarder tick failed; will retry on next interval");
            }

            try { await Task.Delay(tickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
