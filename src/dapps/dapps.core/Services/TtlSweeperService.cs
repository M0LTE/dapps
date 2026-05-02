namespace dapps.core.Services;

/// <summary>
/// Periodically deletes offers and messages whose ttl has elapsed. Runs
/// independently of the forwarder so a node that's not actively forwarding
/// (no neighbours, AGW down, etc.) still ages out stale rows instead of
/// holding them forever.
///
/// Rows with no ttl set are exempt — those are local app submissions
/// without an expiry, or pre-A1 rows from before TTL was tracked.
/// </summary>
public class TtlSweeperService(
    Database database,
    TimeProvider timeProvider,
    ILogger<TtlSweeperService> logger) : BackgroundService
{
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer takes a TimeProvider in .NET 8 — FakeTimeProvider
        // can drive the WaitForNextTickAsync loop deterministically in
        // tests via Advance().
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);

        // Run immediately on startup so a freshly-restarted node clears
        // out anything that expired while it was down.
        await SweepOnce();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnce();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
    }

    private async Task SweepOnce()
    {
        try
        {
            var deleted = await database.DeleteExpired(timeProvider.GetUtcNow().UtcDateTime);
            if (deleted > 0)
            {
                logger.LogInformation("TTL sweeper deleted {0} expired row(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TTL sweeper threw");
        }
    }
}
