using System.Buffers.Binary;
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
/// running on a TCP loopback socket. The fake answers the X-register
/// the bearer issues at startup, then we feed it pre-canned 'U' frames
/// to verify the bearer parses and yields beacons. Validates Plan B's
/// AX.25 path end-to-end at the AGW frame layer — without a real BPQ.
/// </summary>
public sealed class AgwUiDiscoveryTests
{
    [Fact]
    public async Task StartAndAnnounce_WritesXRegisterMonitorAndUiFrames()
    {
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port,
            ourCallsign: "M0SEND", bpqPort: 0, NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync(cts.Token);
        await bearer.AnnounceAsync(
            new BeaconFrame("M0SEND", 0, 600, new AgwBearerHint(0)),
            cts.Token);

        // Drain whatever frames the bearer wrote: X (register), m (monitor toggle), M (announce).
        var frames = await server.WaitForFramesAsync(3, TimeSpan.FromSeconds(3));

        frames.Should().HaveCount(3);
        frames[0].Kind.Should().Be('X');
        frames[0].CallFrom.Should().Be("M0SEND");
        frames[1].Kind.Should().Be('m');
        frames[1].Payload[0].Should().Be(1, "monitor toggle '1' = on");
        frames[2].Kind.Should().Be('M');
        frames[2].CallFrom.Should().Be("M0SEND");
        Encoding.ASCII.GetString(frames[2].Payload).Should().StartWith("DAPPS v1");
    }

    [Fact]
    public async Task ListenAsync_YieldsBeaconWhenPeerSendsUi()
    {
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port,
            ourCallsign: "M0RECV", bpqPort: 1, NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);

        await bearer.StartAsync(cts.Token);

        // Server pushes a 'U' frame carrying a peer's beacon — what BPQ
        // would deliver while monitor mode is on after another station
        // sends an AX.25 UI frame.
        var beaconText = "DAPPS v1 callsign=G7XYZ-9 hops=1 ttl=600";
        await server.SendFrameToClientAsync(new AgwFrame(
            Port: 1, Kind: 'U', Pid: 0xF0,
            CallFrom: "G7XYZ-9", CallTo: "DAPPS",
            Payload: Encoding.ASCII.GetBytes(beaconText)));

        var heard = new TaskCompletionSource<BeaconFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var b in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                heard.TrySetResult(b);
                break;
            }
        }, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        got.Callsign.Should().Be("G7XYZ-9");
        got.Hops.Should().Be(1);
        got.Bearer.Should().BeOfType<AgwBearerHint>();
        ((AgwBearerHint)got.Bearer).BpqPort.Should().Be(1);
    }

    [Fact]
    public async Task ListenAsync_IgnoresOwnEchoedBeaconAndNonUiKinds()
    {
        using var server = new FakeAgwServer();
        await using var bearer = new AgwUiDiscoveryBearer(
            server.Host, server.Port,
            ourCallsign: "M0SELF", bpqPort: 0, NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.AcceptOneAsync(cts.Token), cts.Token);
        await bearer.StartAsync(cts.Token);

        // Echo of our own outbound — must NOT yield.
        await server.SendFrameToClientAsync(new AgwFrame(
            0, 'U', 0xF0, "M0SELF", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=M0SELF hops=0 ttl=600")));

        // Other monitor frame kinds also fire during monitor mode —
        // 'T' (echoed UI), 'S' (supervisory), 'I' (info). Bearer must
        // ignore them.
        await server.SendFrameToClientAsync(new AgwFrame(
            0, 'T', 0xF0, "G7XYZ-9", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=0 ttl=600")));

        // Real beacon from a real peer — must yield this one.
        await server.SendFrameToClientAsync(new AgwFrame(
            0, 'U', 0xF0, "G7XYZ-9", "DAPPS",
            Encoding.ASCII.GetBytes("DAPPS v1 callsign=G7XYZ-9 hops=2 ttl=600")));

        var heard = new TaskCompletionSource<BeaconFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var b in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                heard.TrySetResult(b);
                break;
            }
        }, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        got.Callsign.Should().Be("G7XYZ-9");
        got.Hops.Should().Be(2, "the bearer must skip the self-echo and the wrong-kind frame and surface only the genuine peer beacon");
    }

    /// <summary>
    /// Tiny TCP server that pretends to be BPQ's AGW listener, just
    /// enough to exercise the bearer. Sends a single canned X-register
    /// ack on connect; thereafter relays per-test fixture sends and
    /// captures everything the client writes.
    /// </summary>
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

            // Read loop: capture every frame the client sends, and
            // auto-reply to the X register with a 1-byte 0x01.
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
                catch { /* expected on tear-down */ }
            }, ct);
        }

        public async Task SendFrameToClientAsync(AgwFrame frame)
        {
            // The server may not have accepted yet — give it a moment.
            for (var i = 0; i < 30 && _framing is null; i++) await Task.Delay(50);
            await _framing!.WriteFrameAsync(frame, CancellationToken.None);
        }

        public async Task<IReadOnlyList<AgwFrame>> WaitForFramesAsync(int n, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_received)
                {
                    if (_received.Count >= n) return _received.ToList();
                }
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
