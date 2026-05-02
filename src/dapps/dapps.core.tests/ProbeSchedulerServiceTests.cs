using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests for <see cref="ProbeSchedulerService"/>'s pure helpers — target
/// enumeration, port resolution, result recording. The
/// <c>ExecuteAsync</c> loop itself isn't exercised here; that's
/// covered indirectly via the on-demand SweepAsync entry point and
/// directly via integration coverage when the fixture is available.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class ProbeSchedulerServiceTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-probe-sched-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbProbedNode>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbDiscoveredPeer>();
        }

        database = new Database(NullLogger<Database>.Instance,
            MakeOptions(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 }));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EnumerateTargets_NoNeighboursOrPeers_ReturnsEmpty()
    {
        var sched = MakeScheduler(new RecordingTransport());

        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourWithBpqPort_UsesNeighbourPort()
    {
        await database.UpsertNeighbour("N0THEM-9", bpqPort: 3);

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProbeSchedulerService.ProbeTarget("N0THEM-9", 3));
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourWithUdpEndpoint_Skipped()
    {
        // UDP-routed neighbours can't be probed via AGW. Skipping them
        // here is the only signal the prober has — there's no AGW
        // alternative path to fall back to for a UDP-only neighbour.
        await database.UpsertNeighbour("N0UDP-9", bpqPort: null, udpEndpoint: "127.0.0.1:1880");

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourWithoutBpqPort_FallsBackToDefault()
    {
        await database.UpsertNeighbour("N0NULL-9", bpqPort: null);

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().ContainSingle().Which.BpqPort.Should().Be(7);
    }

    [Fact]
    public async Task EnumerateTargets_AgwDiscoveredPeer_Included()
    {
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0BCN-9",
            Bearer = "agw",
            ChannelKey = "1",
            BpqPort = 1,
            LinkClass = LinkClass.VhfUhfFm,
            CostHint = 1,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProbeSchedulerService.ProbeTarget("N0BCN-9", 1));
    }

    [Fact]
    public async Task EnumerateTargets_UdpDiscoveredPeer_Skipped()
    {
        // UDP-bearer beacons populate DbDiscoveredPeer too, but the
        // probe surface is AGW-only — those rows must not contribute.
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0UDP-9",
            Bearer = "udp",
            ChannelKey = "239.0.0.1:54321",
            UdpEndpoint = "127.0.0.1:1880",
            LinkClass = LinkClass.LanMulticast,
            CostHint = 8,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateTargets_NeighbourAndPeerForSameCallsign_NeighbourPortWins()
    {
        // A manual /Neighbours add represents the operator's explicit
        // route; if a passing beacon also names that callsign on a
        // different port, we trust the operator.
        await database.UpsertNeighbour("N0BOTH-9", bpqPort: 5);
        await database.UpsertDiscoveredPeer(new DbDiscoveredPeer
        {
            Callsign = "N0BOTH-9",
            Bearer = "agw",
            ChannelKey = "0",
            BpqPort = 0,
            LinkClass = LinkClass.VhfUhfFm,
            CostHint = 1,
            TtlSeconds = 600,
            LastSeen = DateTime.UtcNow,
        });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().ContainSingle().Which.BpqPort.Should().Be(5);
    }

    [Fact]
    public async Task EnumerateTargets_OptOutRow_Excluded()
    {
        await database.UpsertNeighbour("N0OUT-9", bpqPort: 1);
        await database.UpsertProbedNode(new DbProbedNode { Callsign = "N0OUT-9", OptOut = true });

        var sched = MakeScheduler(new RecordingTransport());
        var targets = await sched.EnumerateTargets(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 });

        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task SweepAsync_ProbesEveryTarget_RecordsResultPerCallsign()
    {
        await database.UpsertNeighbour("N0AAA-9", bpqPort: 1);
        await database.UpsertNeighbour("N0BBB-9", bpqPort: 2);
        await database.UpsertNeighbour("N0CCC-9", bpqPort: 3);

        var transport = new RecordingTransport(
            ("N0AAA-9", "DAPPSv1>\n"),
            ("N0BBB-9", "no prompt here"),
            ("N0CCC-9", "DAPPSv1>\n"));
        var sched = MakeScheduler(transport,
            // Tests must not actually wait 5–30 seconds between probes.
            minInterProbeDelay: TimeSpan.Zero,
            maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.SweepAsync(
            new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 },
            CancellationToken.None);

        var rows = (await database.GetProbedNodes()).ToDictionary(r => r.Callsign);
        rows.Should().HaveCount(3);
        rows["N0AAA-9"].LastSuccessAt.Should().NotBeNull();
        rows["N0AAA-9"].SuccessCount.Should().Be(1);
        rows["N0AAA-9"].ConsecutiveFailures.Should().Be(0);
        rows["N0AAA-9"].LastBpqPort.Should().Be(1);
        rows["N0BBB-9"].LastSuccessAt.Should().BeNull();
        rows["N0BBB-9"].ConsecutiveFailures.Should().Be(1);
        rows["N0BBB-9"].LastError.Should().Contain("DAPPSv1>");
        rows["N0CCC-9"].LastSuccessAt.Should().NotBeNull();

        transport.Connects.Should().HaveCount(3);
        transport.Connects.Select(c => c.Remote).Should().BeEquivalentTo(["N0AAA-9", "N0BBB-9", "N0CCC-9"]);
    }

    [Fact]
    public async Task SweepAsync_PreservesOptOutAcrossUpdate()
    {
        // OptOut on a previously-probed row would normally exclude it
        // from the sweep, but if the operator later un-flips it the row
        // already exists with prior result data. The recorder must not
        // wipe OptOut when applying a result. (Belt-and-braces: the
        // sweep filter excludes OptOut rows up front, so this only
        // matters for an explicit on-demand run.)
        await database.UpsertNeighbour("N0OUT-9", bpqPort: 1);
        await database.UpsertProbedNode(new DbProbedNode { Callsign = "N0OUT-9", OptOut = true });

        var sched = MakeScheduler(new RecordingTransport(("N0OUT-9", "DAPPSv1>\n")),
            minInterProbeDelay: TimeSpan.Zero, maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.ProbeAndRecordAsync("N0US", "N0OUT-9", 1, CancellationToken.None);

        var row = await database.GetProbedNode("N0OUT-9");
        row.Should().NotBeNull();
        row!.OptOut.Should().BeTrue();
        row.LastSuccessAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_RepeatedFailures_BumpConsecutiveFailures()
    {
        await database.UpsertNeighbour("N0DEAD-9", bpqPort: 1);
        var transport = new RecordingTransport(
            ("N0DEAD-9", "no prompt"),
            ("N0DEAD-9", "no prompt"));
        var sched = MakeScheduler(transport,
            minInterProbeDelay: TimeSpan.Zero, maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 }, CancellationToken.None);
        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 }, CancellationToken.None);

        var row = await database.GetProbedNode("N0DEAD-9");
        row.Should().NotBeNull();
        row!.ConsecutiveFailures.Should().Be(2);
        row.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task SweepAsync_SuccessAfterFailure_ResetsCounter()
    {
        await database.UpsertNeighbour("N0FLAP-9", bpqPort: 1);
        var transport = new RecordingTransport(
            ("N0FLAP-9", "no prompt"),
            ("N0FLAP-9", "DAPPSv1>\n"));
        var sched = MakeScheduler(transport,
            minInterProbeDelay: TimeSpan.Zero, maxInterProbeDelay: TimeSpan.FromMilliseconds(1));

        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 }, CancellationToken.None);
        await sched.SweepAsync(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7 }, CancellationToken.None);

        var row = await database.GetProbedNode("N0FLAP-9");
        row.Should().NotBeNull();
        row!.ConsecutiveFailures.Should().Be(0);
        row.SuccessCount.Should().Be(1);
        row.LastError.Should().BeEmpty();
    }

    private ProbeSchedulerService MakeScheduler(
        IDappsOutboundTransport transport,
        TimeSpan? minInterProbeDelay = null,
        TimeSpan? maxInterProbeDelay = null)
    {
        var prober = new NodeProber(transport, NullLoggerFactory.Instance, NullLogger<NodeProber>.Instance);
        var opts = MakeOptions(new SystemOptions { Callsign = "N0US", DefaultBpqPort = 7, ProbingEnabled = true });
        return new ProbeSchedulerService(prober, database, opts, NullLogger<ProbeSchedulerService>.Instance)
        {
            // Tests skip the 15-minute startup grace; ExecuteAsync isn't
            // exercised here, but if it were, this lets the loop fire
            // immediately.
            StartupDelay = TimeSpan.Zero,
            MinInterProbeDelay = minInterProbeDelay ?? TimeSpan.Zero,
            MaxInterProbeDelay = maxInterProbeDelay ?? TimeSpan.FromMilliseconds(1),
        };
    }

    private static IOptionsMonitor<T> MakeOptions<T>(T value) => new TestOptionsMonitor<T>(value);

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Test transport that hands back a different canned byte
    /// stream per <paramref name="cannedByCallsign"/> entry, in order.
    /// Records each connect attempt for assertions.</summary>
    private sealed class RecordingTransport(params (string Remote, string Reply)[] cannedByCallsign) : IDappsOutboundTransport
    {
        private readonly Queue<(string Remote, string Reply)> _queue = new(cannedByCallsign);
        public List<(string Remote, int Port)> Connects { get; } = new();

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bpqPortNumber, CancellationToken stoppingToken)
        {
            Connects.Add((remoteCallsign, bpqPortNumber));
            // Best-effort: prefer the next-in-queue entry that matches
            // the requested remote, falling back to "no prompt" so the
            // prober reports failure rather than wedging on EOF.
            string reply = "";
            if (_queue.Count > 0)
            {
                var next = _queue.Dequeue();
                reply = string.Equals(next.Remote, remoteCallsign, StringComparison.OrdinalIgnoreCase)
                    ? next.Reply
                    : $"no canned reply for {remoteCallsign}";
            }
            var bytes = Encoding.UTF8.GetBytes(reply);
            return Task.FromResult<IDappsConnection>(new FakeConnection(new MemoryStream(bytes)));
        }

        private sealed class FakeConnection(Stream stream) : IDappsConnection
        {
            public Stream Stream { get; } = stream;
            public ValueTask DisposeAsync() { Stream.Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
