using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Discovery;
using dapps.client.Transport.Agw;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="AgwUiDiscoveryBearer"/> against a fake AGW server
/// running on a TCP loopback socket. Validates the AX.25-side discovery
/// path end-to-end at the AGW frame layer — without a real BPQ.
/// </summary>
public sealed class AgwUiDiscoveryTests
{
    private static DiscoveryChannelInfo Channel(int id, byte port) =>
        new(id, "agw", port.ToString(), LinkClass.VhfUhfFm,
            BeaconIntervalSeconds: 1800, AdvertisedTtlSeconds: 5400, CostHint: 5);

    [Fact]
    public async Task StartAndAnnounce_WritesXRegisterMonitorAndUiFrames()
    {
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port, ourCallsign: "M0SEND", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync([Channel(1, 0)], cts.Token);
        await bearer.AnnounceAsync(
            new BeaconFrame("M0SEND", 0, 600, new AgwBearerHint(0)),
            "0", cts.Token);

        // X (register), m (monitor enable), M (announce) — exactly three.
        var frames = await server.WaitForFramesAsync(3, TimeSpan.FromSeconds(3));
        frames.Should().HaveCount(3);
        frames[0].Kind.Should().Be('X');
        frames[1].Kind.Should().Be('m');
        frames[1].Payload[0].Should().Be(1);
        frames[2].Kind.Should().Be('M');
        Encoding.ASCII.GetString(frames[2].Payload).Should().StartWith("DAPPS v1");
    }

    [Fact]
    public async Task ListenAsync_YieldsBeaconWhenPeerSendsUi()
    {
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port, ourCallsign: "M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync([Channel(1, 1)], cts.Token);

        var beaconText = "DAPPS v1 callsign=G7XYZ-9 hops=1 ttl=600";
        await server.SendFrameToClientAsync(new AgwFrame(
            Port: 1, Kind: 'U', Pid: 0xF0,
            CallFrom: "G7XYZ-9", CallTo: "DAPPS",
            Payload: Encoding.ASCII.GetBytes(beaconText)));

        var heard = new TaskCompletionSource<ReceivedBeacon>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                heard.TrySetResult(rb);
                break;
            }
        }, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        got.Beacon.Callsign.Should().Be("G7XYZ-9");
        got.Beacon.Hops.Should().Be(1);
        got.Beacon.Bearer.Should().BeOfType<AgwBearerHint>();
        ((AgwBearerHint)got.Beacon.Bearer).BpqPort.Should().Be(1);
        got.ChannelKey.Should().Be("1");
    }

    [Fact]
    public async Task SolicitAsync_WritesUiFrameWithSolicitMagic()
    {
        // Plan B6.2 — emit side. Solicit goes out as a UI ('M') frame
        // with the solicit codec's longer magic prefix, distinguishable
        // from a beacon's payload at the codec layer.
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port, ourCallsign: "M0SEND", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync([Channel(1, 0)], cts.Token);
        await bearer.SolicitAsync(new SolicitFrame("M0SEND"), "0", cts.Token);

        // X (register), m (monitor enable), M (solicit) — three frames,
        // last one a UI emit with the solicit prefix.
        var frames = await server.WaitForFramesAsync(3, TimeSpan.FromSeconds(3));
        frames.Should().HaveCount(3);
        frames[2].Kind.Should().Be('M');
        Encoding.ASCII.GetString(frames[2].Payload).Should().StartWith("DAPPS v1 solicit ");
    }

    [Fact]
    public async Task ListenAsync_YieldsReceivedSolicitWhenPeerSolicits()
    {
        // Plan B6.2 — receive side. An inbound UI frame whose payload
        // matches the solicit codec must surface as a ReceivedSolicit,
        // not a ReceivedBeacon, so DiscoveryService can dispatch the
        // delayed-reply path instead of upserting a peer row.
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port, ourCallsign: "M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync([Channel(1, 1)], cts.Token);

        await server.SendFrameToClientAsync(new AgwFrame(
            Port: 1, Kind: 'U', Pid: 0xF0,
            CallFrom: "G7XYZ-9", CallTo: "DAPPS",
            Payload: Encoding.ASCII.GetBytes("DAPPS v1 solicit callsign=G7XYZ-9")));

        var heard = new TaskCompletionSource<ReceivedSolicit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedSolicit rs) continue;
                heard.TrySetResult(rs);
                break;
            }
        }, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        got.Solicit.Callsign.Should().Be("G7XYZ-9");
        got.ChannelKey.Should().Be("1");
    }

    [Fact]
    public async Task MultiplePorts_OneBearerInstance_DistinguishesByChannelKey()
    {
        // A node with VHF on BPQ port 1 and AXIP on BPQ port 3 shares
        // one AGW socket. Beacons on each port must be tagged with the
        // matching channel key when yielded.
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port, ourCallsign: "M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync([Channel(1, 1), Channel(2, 3)], cts.Token);

        await server.SendFrameToClientAsync(new AgwFrame(
            1, 'U', 0xF0, "G7XYZ-9", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")));
        await server.SendFrameToClientAsync(new AgwFrame(
            3, 'U', 0xF0, "G0AXP", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=G0AXP hops=2 ttl=600")));

        var seen = new List<ReceivedBeacon>();
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                lock (seen) seen.Add(rb);
                if (seen.Count >= 2) break;
            }
        }, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen) if (seen.Count >= 2) break;
            await Task.Delay(50, cts.Token);
        }

        lock (seen)
        {
            seen.Should().HaveCount(2);
            seen.Single(s => s.Beacon.Callsign == "G7XYZ-9").ChannelKey.Should().Be("1");
            seen.Single(s => s.Beacon.Callsign == "G0AXP").ChannelKey.Should().Be("3");
        }
    }

    [Fact]
    public async Task ListenAsync_IgnoresOwnEchoedBeaconAndNonUiKinds()
    {
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port, ourCallsign: "M0SELF", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);
        await bearer.StartAsync([Channel(1, 0)], cts.Token);

        // Self-echo: must NOT yield.
        await server.SendFrameToClientAsync(new AgwFrame(
            0, 'U', 0xF0, "M0SELF", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=M0SELF hops=0 ttl=600")));
        // Wrong kind 'T' (echoed UI in monitor mode): must NOT yield.
        await server.SendFrameToClientAsync(new AgwFrame(
            0, 'T', 0xF0, "G7XYZ-9", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")));
        // Real beacon: must yield.
        await server.SendFrameToClientAsync(new AgwFrame(
            0, 'U', 0xF0, "G7XYZ-9", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=2 ttl=600")));

        var heard = new TaskCompletionSource<ReceivedBeacon>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var frame in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                if (frame is not ReceivedBeacon rb) continue;
                heard.TrySetResult(rb);
                break;
            }
        }, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        got.Beacon.Callsign.Should().Be("G7XYZ-9");
        got.Beacon.Hops.Should().Be(2,
            "the bearer must skip the self-echo and the wrong-kind frame and surface only the genuine peer beacon");
    }

    private sealed class FakeAgwServer : IDisposable
    {
        private readonly TcpListener _listener;
        private TcpClient? _accepted;
        private NetworkStream? _stream;
        private AgwFrameTransport? _framing;
        private readonly List<AgwFrame> _received = [];

        public string Host => "127.0.0.1";
        public int Port { get; }

        public FakeAgwServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public async Task AcceptOneAsync(CancellationToken ct)
        {
            _accepted = await _listener.AcceptTcpClientAsync(ct);
            _stream = _accepted.GetStream();
            _framing = new AgwFrameTransport(_stream);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var f = await _framing.ReadFrameAsync(ct);
                        lock (_received) _received.Add(f);
                        if (f.Kind == 'X')
                        {
                            await _framing.WriteFrameAsync(
                                new AgwFrame(0, 'X', 0, "", "", [0x01]),
                                ct);
                        }
                    }
                }
                catch { /* tear-down */ }
            }, ct);
        }

        public async Task SendFrameToClientAsync(AgwFrame frame)
        {
            for (var i = 0; i < 30 && _framing is null; i++) await Task.Delay(50);
            await _framing!.WriteFrameAsync(frame, CancellationToken.None);
        }

        public async Task<IReadOnlyList<AgwFrame>> WaitForFramesAsync(int n, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_received) if (_received.Count >= n) return _received.ToList();
                await Task.Delay(50);
            }
            lock (_received) return _received.ToList();
        }

        public void Dispose()
        {
            _accepted?.Dispose();
            _listener.Stop();
        }
    }
}
