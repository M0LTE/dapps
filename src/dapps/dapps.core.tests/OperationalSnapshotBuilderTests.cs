using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan C3 PR-B — `OperationalSnapshotBuilder` is the single source of
/// truth feeding both <c>/Operational</c> and the periodic MQTT
/// heartbeat. These tests pin its composition so a future field
/// rename / removal can't silently drift the operator-visible
/// shape across the two surfaces.
///
/// The TCP probes (BPQ + MQTT) intentionally run against unbound
/// loopback ports in the test fixture — they'll fail fast and the
/// resulting snapshot's <c>Status</c> = "degraded" is the easiest
/// thing to assert against.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OperationalSnapshotBuilderTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-snap-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using var c = DbInfo.GetConnection();
        c.CreateTable<DbMessage>();
        c.CreateTable<DbNeighbour>();
        c.CreateTable<DbDiscoveredPeer>();
        c.CreateTable<DbDiscoveryChannel>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BuildAsync_ComposesCountersQueuesAirtimeAndStatus()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G7TST-9",
            NodeHost = "127.0.0.1",
            // Pick ports nothing will be listening on so the TCP probes
            // resolve to "unreachable" and Status flips to degraded.
            // Nothing in this test asserts the probes succeed.
            AgwPort = 1,
            MqttPort = 1,
            DiscoveryAirtimeBudgetSecondsPerHour = 60,
        });

        var metrics = new OperationalMetrics(clock);
        metrics.RecordForwardSuccess("aaa1234", "G7PEER", 100);
        metrics.RecordProbeOutcome("G7PEER", success: true, error: null);
        metrics.RecordPollOutcome("G7PEER", success: false, messagesDrained: 0, error: "no banner");
        metrics.RecordRouteLearned("G7DEST", "G7HOP-1");

        var airtime = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        airtime.TryReserve(2.0, "beacon udp/x").Should().BeTrue();

        var db = new Database(NullLogger<Database>.Instance, opts);
        await db.UpsertNeighbour("G7PEER-9", bpqPort: 1);

        var builder = new OperationalSnapshotBuilder(opts, metrics, airtime, db, clock);
        var snap = await builder.BuildAsync(TestContext.Current.CancellationToken);

        snap.Callsign.Should().Be("G7TST-9");
        snap.CallsignConfigured.Should().BeTrue();
        snap.Status.Should().Be("degraded", "BPQ + MQTT probes target unbound ports");
        snap.BpqAgwReachable.Should().BeFalse();
        snap.MqttBrokerUp.Should().BeFalse();

        snap.ForwardSuccess.Should().Be(1);
        snap.ProbeAttempts.Should().Be(1);
        snap.ProbeSuccess.Should().Be(1);
        snap.PollAttempts.Should().Be(1);
        snap.PollFailure.Should().Be(1);
        snap.RoutesLearned.Should().Be(1);

        snap.NeighbourCount.Should().Be(1);
        snap.LastForwardSuccessAt.Should().NotBeNull();

        snap.AirtimeBudgetSecondsPerHour.Should().Be(60);
        snap.AirtimeConsumedSecondsLastHour.Should().BeApproximately(2.0, 0.001);

        snap.RecentEvents.Should().NotBeEmpty();
        snap.RecentEvents.Select(e => e.Kind).Should()
            .Contain(new[] { "forward.ok", "probe.ok", "poll.fail", "route.learned" });
    }

    [Fact]
    public async Task BuildAsync_PlaceholderCallsign_FlagsDegradedEvenIfDepsCouldBeUp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-01T12:00:00Z"));
        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",  // placeholder
            NodeHost = "127.0.0.1",
            AgwPort = 1,
            MqttPort = 1,
        });
        var metrics = new OperationalMetrics(clock);
        var airtime = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        var db = new Database(NullLogger<Database>.Instance, opts);

        var builder = new OperationalSnapshotBuilder(opts, metrics, airtime, db, clock);
        var snap = await builder.BuildAsync(TestContext.Current.CancellationToken);

        snap.CallsignConfigured.Should().BeFalse();
        snap.Status.Should().Be("degraded");
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
