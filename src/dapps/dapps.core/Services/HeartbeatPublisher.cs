using System.Text.Json;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan C3 PR-B — periodic MQTT heartbeat publish. Every
/// <see cref="SystemOptions.HeartbeatIntervalSeconds"/> seconds (default
/// 60), serialises an <see cref="OperationalSnapshot"/> and publishes
/// it as a retained message on <c>dapps/metrics/heartbeat</c>.
///
/// "Retained" is the trick: a Home Assistant or other operator-side
/// consumer connecting at any time gets the latest snapshot
/// immediately, without waiting up to a full interval for the next
/// publish. The same JSON shape as <c>GET /Operational</c>.
///
/// Default on (<see cref="SystemOptions.HeartbeatEnabled"/> = true) —
/// publishing to a topic on a broker that's already running for the
/// app interface is essentially free, and operators who don't want
/// it can flip the toggle.
/// </summary>
public sealed class HeartbeatPublisher(
    IOptionsMonitor<SystemOptions> options,
    OperationalSnapshotBuilder snapshotBuilder,
    MqttBrokerService mqtt,
    TimeProvider timeProvider,
    ILogger<HeartbeatPublisher> logger) : BackgroundService
{
    public const string Topic = "dapps/metrics/heartbeat";

    /// <summary>Delay before the first heartbeat after startup. The
    /// MQTT broker takes a moment to bind; publishing into a half-
    /// constructed broker would silently no-op.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often to recheck the toggle when disabled, so
    /// flipping <see cref="SystemOptions.HeartbeatEnabled"/> on via
    /// /Config doesn't require a restart.</summary>
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            if (!opts.HeartbeatEnabled)
            {
                try { await Task.Delay(DisabledPollInterval, timeProvider, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                var snapshot = await snapshotBuilder.BuildAsync(stoppingToken);
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                await mqtt.PublishRetainedAsync(Topic, payload, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Heartbeat publish failed");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(10, opts.HeartbeatIntervalSeconds));
            try { await Task.Delay(interval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
