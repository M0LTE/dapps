using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests the <c>DbDiscoveredPeer</c> + <c>DbDiscoveryChannel</c> storage
/// paths that <see cref="DiscoveryService"/> calls into. Composite-key
/// upsert (peers), per-channel CRUD (channels), and age-out semantics.
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
            c.CreateTable<DbDiscoveryChannel>();
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

    // ── DbDiscoveredPeer ─────────────────────────────────────────

    [Fact]
    public async Task UpsertDiscoveredPeer_FirstSighting_InsertsRow()
    {
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9",
            Bearer = "udp",
            ChannelKey = "239.42.42.42:1881",
            ChannelId = 1,
            LinkClass = LinkClass.LanMulticast,
            CostHint = 1,
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
    public async Task UpsertDiscoveredPeer_SameCallsignBearerChannel_RefreshesNotDuplicates()
    {
        var t1 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(10);

        DbDiscoveredPeer make(DateTime when) => new()
        {
            Callsign = "G7XYZ-9", Bearer = "udp", ChannelKey = "g:1",
            ChannelId = 1, LinkClass = LinkClass.LanMulticast, CostHint = 1,
            Hops = 0, TtlSeconds = 600, UdpEndpoint = "10.0.0.5:1881", LastSeen = when,
        };

        await database.UpsertDiscoveredPeer(make(t1));
        await database.UpsertDiscoveredPeer(make(t2));

        var rows = await database.GetDiscoveredPeers();
        rows.Should().ContainSingle("upsert MUST NOT duplicate (callsign,bearer,channelKey)");
        rows[0].LastSeen.Should().Be(t2);
    }

    [Fact]
    public async Task UpsertDiscoveredPeer_SameCallsignDifferentChannelKey_BothPersisted()
    {
        // Same peer reachable on two different multicast groups, e.g.
        // wired LAN segment and wireless segment - must occupy two rows.
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9", Bearer = "udp", ChannelKey = "g:1",
            LinkClass = LinkClass.LanMulticast, CostHint = 1, Hops = 0,
            TtlSeconds = 600, UdpEndpoint = "x:1", LastSeen = DateTime.UtcNow,
        });
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "G7XYZ-9", Bearer = "udp", ChannelKey = "g:2",
            LinkClass = LinkClass.LanMulticast, CostHint = 1, Hops = 0,
            TtlSeconds = 600, UdpEndpoint = "y:2", LastSeen = DateTime.UtcNow,
        });

        (await database.GetDiscoveredPeers()).Should().HaveCount(2);
    }

    [Fact]
    public async Task AgeOutDiscoveredPeers_DropsRowsPastTheirTtl()
    {
        var t0 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "STALE", Bearer = "udp", ChannelKey = "x",
            LinkClass = LinkClass.LanMulticast, CostHint = 1, TtlSeconds = 600,
            LastSeen = t0.AddHours(-2),
        });
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "FRESH", Bearer = "udp", ChannelKey = "y",
            LinkClass = LinkClass.LanMulticast, CostHint = 1, TtlSeconds = 3600,
            LastSeen = t0.AddSeconds(-30),
        });

        (await database.AgeOutDiscoveredPeers(t0)).Should().HaveCount(1);
        var remaining = await database.GetDiscoveredPeers();
        remaining.Should().ContainSingle().Which.Callsign.Should().Be("FRESH");
    }

    // ── DbDiscoveryChannel ───────────────────────────────────────

    [Fact]
    public async Task UpsertDiscoveryChannel_AppliesLinkClassDefaults()
    {
        // Operator gives just bearer + key + class; cadence/ttl/cost
        // fill in from the LinkClass.
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw",
            ChannelKey = "1",
            LinkClass = LinkClass.VhfUhfFm,
        });

        var rows = await database.GetDiscoveryChannels();
        var row = rows.Should().ContainSingle().Subject;
        row.BeaconIntervalSeconds.Should().Be(LinkClassDefaults.BeaconIntervalSeconds(LinkClass.VhfUhfFm));
        row.AdvertisedTtlSeconds.Should().Be(LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.VhfUhfFm));
        row.CostHint.Should().Be(LinkClassDefaults.CostHint(LinkClass.VhfUhfFm));
    }

    [Fact]
    public async Task UpsertDiscoveryChannel_PreservesExplicitOverrides()
    {
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm,
            BeaconIntervalSeconds = 60,           // operator wants chatty for testing
            AdvertisedTtlSeconds = 240,
            CostHint = 2,                          // and treats this VHF link as cheap
        });

        var row = (await database.GetDiscoveryChannels()).Single();
        row.BeaconIntervalSeconds.Should().Be(60);
        row.AdvertisedTtlSeconds.Should().Be(240);
        row.CostHint.Should().Be(2);
    }

    [Fact]
    public async Task UpsertDiscoveryChannel_SameBearerAndKey_UpdatesInPlace()
    {
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm,
            Notes = "first attempt",
        });
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.Hf,
            Notes = "actually it's HF",
        });

        var rows = await database.GetDiscoveryChannels();
        rows.Should().ContainSingle("re-upsert must not duplicate (bearer,channelKey)");
        rows[0].LinkClass.Should().Be(LinkClass.Hf);
        rows[0].Notes.Should().Be("actually it's HF");
    }

    [Fact]
    public async Task RemoveDiscoveryChannel_Existing_ReturnsTrue()
    {
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm,
        });
        var rows = await database.GetDiscoveryChannels();
        var deleted = await database.RemoveDiscoveryChannel(rows[0].Id);
        deleted.Should().BeTrue();
        (await database.GetDiscoveryChannels()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiscoveryChannels_OrderedByCostHint()
    {
        // RF-first ordering per the project ethos: VHF/UHF cheapest,
        // then HF, then LAN multicast (testing-scope IP), then
        // generic internet last-resort.
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "udp", ChannelKey = "ip", LinkClass = LinkClass.InternetIp,
        });
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "1", LinkClass = LinkClass.VhfUhfFm,
        });
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "udp", ChannelKey = "g1", LinkClass = LinkClass.LanMulticast,
        });
        await database.UpsertDiscoveryChannel(new DbDiscoveryChannel
        {
            Bearer = "agw", ChannelKey = "5", LinkClass = LinkClass.Hf,
        });

        var rows = await database.GetDiscoveryChannels();
        rows.Select(r => r.LinkClass).Should().Equal(
            LinkClass.VhfUhfFm, LinkClass.Hf, LinkClass.LanMulticast, LinkClass.InternetIp);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
