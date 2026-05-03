using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// Audit log fundamentals: rows persist, the disabled flag short-
/// circuits writes, the retention sweep deletes rows older than the
/// configured window, the listing API filters by kind / target /
/// only-failures correctly.
///
/// Doesn't exercise the MQTT publish path - that's gated by the
/// TransmissionAuditMqttPublish flag and routes through the broker
/// service, which the integration tests will catch.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class TransmissionAuditServiceTests : IAsyncLifetime
{
    private string dbPath = null!;
    private FakeTimeProvider clock = null!;
    private TestOptionsMonitor<SystemOptions> options = null!;
    private TransmissionAuditService audit = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-audit-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbTransmission>();
        }
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        options = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0TEST",
            TransmissionAuditEnabled = true,
            TransmissionAuditRetentionDays = 30,
            TransmissionAuditMqttPublish = false,
        });
        audit = new TransmissionAuditService(
            NullLogger<TransmissionAuditService>.Instance,
            options,
            mqttBroker: null!,  // not exercised since MqttPublish is off
            timeProvider: clock);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RecordAsync_WhenEnabled_PersistsRow()
    {
        await audit.RecordAsync(
            kind: "probe",
            bearer: "agw",
            reason: "scheduled probe sweep",
            success: true,
            targetCallsign: "M0LTE-1",
            channelKey: "0",
            durationMs: 432);

        var rows = await audit.ListRecentAsync();
        rows.Should().HaveCount(1);
        var row = rows[0];
        row.Kind.Should().Be("probe");
        row.Bearer.Should().Be("agw");
        row.Reason.Should().Be("scheduled probe sweep");
        row.TargetCallsign.Should().Be("M0LTE-1");
        row.ChannelKey.Should().Be("0");
        row.DurationMs.Should().Be(432);
        row.Success.Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_WhenDisabled_NoOp()
    {
        options.SetValue(o => o.TransmissionAuditEnabled = false);

        await audit.RecordAsync("probe", "agw", "should not persist", success: true);
        var rows = await audit.ListRecentAsync();
        rows.Should().BeEmpty("the disabled flag short-circuits the insert");
    }

    [Fact]
    public async Task ListRecentAsync_FiltersByKind()
    {
        await audit.RecordAsync("probe", "agw", "p", success: true);
        await audit.RecordAsync("beacon", "agw", "b", success: true);
        await audit.RecordAsync("forward", "agw", "f", success: true);

        var probes = await audit.ListRecentAsync(kinds: new[] { "probe" });
        probes.Should().HaveCount(1);
        probes[0].Kind.Should().Be("probe");

        var probesAndForwards = await audit.ListRecentAsync(kinds: new[] { "probe", "forward" });
        probesAndForwards.Should().HaveCount(2);
        probesAndForwards.Select(r => r.Kind).Should().BeEquivalentTo(new[] { "forward", "probe" });
    }

    [Fact]
    public async Task ListRecentAsync_FiltersByTargetCallsign()
    {
        await audit.RecordAsync("probe", "agw", "to A", success: true, targetCallsign: "M0LTE-1");
        await audit.RecordAsync("probe", "agw", "to B", success: true, targetCallsign: "G7XYZ");
        await audit.RecordAsync("beacon", "agw", "broadcast", success: true);

        var rows = await audit.ListRecentAsync(targetCallsign: "M0LTE-1");
        rows.Should().HaveCount(1);
        rows[0].Reason.Should().Be("to A");
    }

    [Fact]
    public async Task ListRecentAsync_FailuresOnly_FiltersSuccess()
    {
        await audit.RecordAsync("probe", "agw", "good", success: true);
        await audit.RecordAsync("probe", "agw", "bad", success: false, errorTag: "RETRYOUT");

        var failed = await audit.ListRecentAsync(successOnly: false);
        failed.Should().HaveCount(1);
        failed[0].ErrorTag.Should().Be("RETRYOUT");
    }

    [Fact]
    public async Task SweepOldRowsAsync_DeletesRowsOlderThanRetention()
    {
        // Row 1 written at the current FakeTimeProvider time. Then
        // advance the clock 60 days, write a fresh row, sweep with a
        // 30-day window: row 1 should go.
        await audit.RecordAsync("probe", "agw", "old", success: true);

        clock.Advance(TimeSpan.FromDays(60));
        await audit.RecordAsync("probe", "agw", "fresh", success: true);

        var deleted = await audit.SweepOldRowsAsync();
        deleted.Should().Be(1);

        var rows = await audit.ListRecentAsync();
        rows.Should().HaveCount(1);
        rows[0].Reason.Should().Be("fresh");
    }

    [Fact]
    public async Task SweepOldRowsAsync_RetentionZero_KeepsEverything()
    {
        options.SetValue(o => o.TransmissionAuditRetentionDays = 0);

        await audit.RecordAsync("probe", "agw", "ancient", success: true);
        clock.Advance(TimeSpan.FromDays(500));

        var deleted = await audit.SweepOldRowsAsync();
        deleted.Should().Be(0);
        (await audit.ListRecentAsync()).Should().HaveCount(1);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        private T current = value;
        public T CurrentValue => current;
        public T Get(string? name) => current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;

        public void SetValue(Action<T> mutate)
        {
            mutate(current);
        }
    }
}
