namespace dapps.core.Services;

/// <summary>
/// Periodically advances per-(sender, stream-id) cursors past elapsed
/// gap deadlines for opt-in-ordered streams in timeout mode (gt&gt;0).
/// Each tick scans <c>streamrecvstate</c> rows whose <c>GapDeadline</c>
/// has passed; for each, asks the inbox to skip the gap and drain
/// successors.
///
/// Strict streams (gt=0) never set a deadline, so this sweeper ignores
/// them - parked rows stay parked until the missing seq fills in or
/// the regular TTL sweeper drops them via the live message's TTL.
/// </summary>
public sealed class StreamGapSweeperService(
    Database database,
    DatabaseAndMqttInbox inbox,
    TimeProvider timeProvider,
    ILogger<StreamGapSweeperService> logger) : BackgroundService
{
    /// <summary>How often to scan for elapsed deadlines. Same cadence
    /// as the TTL sweeper (1 minute). The granularity is fine for
    /// packet-radio gap timeouts, which are typically minutes-to-tens-
    /// of-minutes - operators expecting sub-second skip behaviour
    /// would not be using packet radio in the first place.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);
        await SweepOnce(stoppingToken);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnce(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepOnce(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var stale = await database.GetStaleStreamGapsAsync(now);
            foreach (var recv in stale)
            {
                try
                {
                    await inbox.SkipGapAsync(recv, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Gap-skip failed for stream {0}|{1}",
                        recv.SenderCallsign, recv.StreamId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "stream gap sweeper threw");
        }
    }
}
