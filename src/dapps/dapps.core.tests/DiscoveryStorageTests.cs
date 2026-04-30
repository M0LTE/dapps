using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests the <c>DbDiscoveredPeer</c> storage path that
/// <see cref="DiscoveryService"/> calls into. Composite-key upsert,
/// list, and age-out semantics are pinned here.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DiscoveryStorageTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-disc-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbDiscoveredPeer>();
        }
        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0CALL" });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UpsertDiscoveredPeer_FirstSighting_InsertsRow()
    {
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9",
            Bearer = "udp",
            Hops = 0,
            TtlSeconds = 600,
            UdpEndpoint = "10.0.0.5:1881",
            LastSeen = DateTime.UtcNow,
        });

        var rows = await database.GetDiscoveredPeers();
        rows.Should().ContainSingle();
        rows[0].Callsign.Should().Be("G7XYZ-9");
        rows[0].UdpEndpoint.Should().Be("10.0.0.5:1881");
    }

    [Fact]
    public async Task UpsertDiscoveredPeer_SameCallsignAndBearerTwice_RefreshesNotDuplicates()
    {
        var t1 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(10);

        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9",
            Bearer = "udp",
            Hops = 0,
            TtlSeconds = 600,
            UdpEndpoint = "10.0.0.5:1881",
            LastSeen = t1,
        });
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9",
            Bearer = "udp",
            Hops = 0,
            TtlSeconds = 600,
            UdpEndpoint = "10.0.0.5:1881",
            LastSeen = t2,
        });

        var rows = await database.GetDiscoveredPeers();
        rows.Should().ContainSingle("upsert MUST NOT duplicate (callsign,bearer)");
        rows[0].LastSeen.Should().Be(t2);
    }

    [Fact]
    public async Task UpsertDiscoveredPeer_SameCallsignDifferentBearer_BothPersisted()
    {
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9", Bearer = "udp", Hops = 0, TtlSeconds = 600,
            UdpEndpoint = "10.0.0.5:1881", LastSeen = DateTime.UtcNow,
        });
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9", Bearer = "agw", Hops = 1, TtlSeconds = 600,
            BpqPort = 2, LastSeen = DateTime.UtcNow,
        });

        var rows = await database.GetDiscoveredPeers();
        rows.Should().HaveCount(2,
            "the same peer reachable via two bearers must occupy two rows so the router can choose");
        rows.Select(r => r.Bearer).Should().BeEquivalentTo(new[] { "udp", "agw" });
    }

    [Fact]
    public async Task AgeOutDiscoveredPeers_DropsRowsPastTheirTtl()
    {
        var t0 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

        // ttl=600s, last seen 2 hours ago — should age out.
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "STALE-1", Bearer = "udp", Hops = 0, TtlSeconds = 600,
            UdpEndpoint = "x:1", LastSeen = t0.AddHours(-2),
        });
        // ttl=3600s, last seen 30s ago — should stay.
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "FRESH-1", Bearer = "udp", Hops = 0, TtlSeconds = 3600,
            UdpEndpoint = "y:1", LastSeen = t0.AddSeconds(-30),
        });

        var aged = await database.AgeOutDiscoveredPeers(t0);
        aged.Should().Be(1);

        var remaining = await database.GetDiscoveredPeers();
        remaining.Should().ContainSingle();
        remaining[0].Callsign.Should().Be("FRESH-1");
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
