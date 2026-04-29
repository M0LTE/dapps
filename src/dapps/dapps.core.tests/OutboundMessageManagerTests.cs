using System.Text;
using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="OutboundMessageManager.DoRun"/> with a fake backhaul
/// that captures the <see cref="BackhaulMessage"/> + <see cref="BackhaulRoute"/>
/// the manager hands off. Lets the test assert TTL decrement and routing
/// without standing up real BPQ or even a real DAPPSv1 session protocol —
/// the seam (Plan A0) makes those concerns the bearer's, not the manager's.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OutboundMessageManagerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private FakeBackhaul backhaul = null!;
    private OutboundMessageManager manager = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-omm-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            // A neighbour entry alone is enough to route messages to
            // app@N0DEST (post-A2 resolver matches base callsigns).
            c.Insert(new DbNeighbour { Callsign = "N0DEST", BpqPort = 0 });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            DefaultBpqPort = 0,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        backhaul = new FakeBackhaul();
        manager = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, [backhaul]);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DoRun_MessageWithNoTtl_ForwardsWithNullTtlOnTheBackhaul()
    {
        InsertMessage(id: "noex001", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Ttl.Should().BeNull();
    }

    [Fact]
    public async Task DoRun_MessageWithTtl_ForwardsWithDecrementedTtl()
    {
        // 30s in queue; 60s ttl → backhaul should see ttl 25..30.
        InsertMessage(id: "fresh01", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-30));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Ttl.Should().BeInRange(25, 30);
    }

    [Fact]
    public async Task DoRun_ExpiredMessage_DropsWithoutCallingBackhaul()
    {
        InsertMessage(id: "expired1", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-120));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().BeEmpty();
        // Row should be deleted so it doesn't get retried.
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("expired1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_ExactlyAtTtlBoundary_DropsAsExpired()
    {
        InsertMessage(id: "ontime1", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-60));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().BeEmpty();
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("ontime1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_DestinationSsidDiffersFromNeighbourSsid_StillRoutesViaBaseCallsignMatch()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = "ssid001",
                Payload = "x"u8.ToArray(),
                Salt = 1L,
                Destination = "app@N0DEST-7",
                SourceCallsign = "N0CALL",
                AdditionalProperties = "{}",
            });
        }

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        var sent = backhaul.Sent.Single();
        sent.Message.Destination.Should().Be("app@N0DEST-7");
        sent.Route.Callsign.Should().Be("N0DEST");
    }

    [Fact]
    public async Task DoRun_NoMatchingNeighbour_LeavesMessageUnforwarded()
    {
        InsertMessage(id: "noroute", ttl: null, createdAt: DateTime.UtcNow);
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update messages set destination=? where id=?", "app@N0OTHER", "noroute");
        }

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().BeEmpty();
        using var conn = DbInfo.GetConnection();
        var row = conn.Find<DbMessage>("noroute");
        row.Should().NotBeNull();
        row!.Forwarded.Should().BeFalse("no neighbour matched, message should sit in queue until ttl expires");
    }

    [Fact]
    public async Task DoRun_MixedExpiredAndFresh_DropsExpiredAndForwardsFresh()
    {
        InsertMessage(id: "expir02", ttl: 5, createdAt: DateTime.UtcNow.AddSeconds(-3600));
        InsertMessage(id: "fresh02", ttl: 600, createdAt: DateTime.UtcNow.AddSeconds(-1));

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        backhaul.Sent.Single().Message.Id.Should().Be("fresh02");

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("expir02").Should().BeNull();
        var fresh = c.Find<DbMessage>("fresh02");
        fresh.Should().NotBeNull();
        fresh!.Forwarded.Should().BeTrue();
    }

    [Fact]
    public async Task DoRun_BackhaulRejection_LeavesMessageUnforwardedForRetry()
    {
        InsertMessage(id: "reject1", ttl: null, createdAt: DateTime.UtcNow);
        backhaul.NextResult = BackhaulSendResult.Fail("simulated bearer error");

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Should().ContainSingle();
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("reject1")!.Forwarded.Should().BeFalse();
    }

    [Fact]
    public async Task DoRun_PassesNeighbourBpqPortToBackhaul()
    {
        // Update the seeded neighbour to a non-default BPQ port byte.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update neighbours set BpqPort=? where callsign=?", 3, "N0DEST");
        }
        InsertMessage(id: "portmsg", ttl: null, createdAt: DateTime.UtcNow);

        await manager.DoRun(TestContext.Current.CancellationToken);

        backhaul.Sent.Single().Route.BpqPort.Should().Be(3);
    }

    private static void InsertMessage(string id, int? ttl, DateTime createdAt)
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = id,
            Payload = Encoding.UTF8.GetBytes("payload-" + id),
            Salt = 1L,
            Destination = "app@N0DEST",
            SourceCallsign = "N0CALL",
            AdditionalProperties = "{}",
            Ttl = ttl,
            CreatedAt = createdAt,
        });
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// In-memory backhaul that captures every send for assertion. The
    /// shape of the capture is the seam payoff over the prior
    /// stream-mocking contraption — tests assert at the message level.
    /// </summary>
    private sealed class FakeBackhaul : IDappsBackhaul
    {
        public List<(BackhaulMessage Message, BackhaulRoute Route, string LocalCallsign)> Sent { get; } = [];
        public BackhaulSendResult NextResult { get; set; } = BackhaulSendResult.Ok();

        public bool CanHandle(BackhaulRoute route) => true;

        public Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message, BackhaulRoute route, string localCallsign, CancellationToken ct)
        {
            Sent.Add((message, route, localCallsign));
            return Task.FromResult(NextResult);
        }
    }
}
