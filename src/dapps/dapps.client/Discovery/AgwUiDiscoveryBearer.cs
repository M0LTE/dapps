using System.Net.Sockets;
using System.Threading.Channels;
using dapps.client.Transport.Agw;
using Microsoft.Extensions.Logging;

namespace dapps.client.Discovery;

/// <summary>
/// Discovery over AGW UI (unconnected) frames. The AGW protocol exposes
/// three primitives we need:
///
///   'X' — register our callsign so BPQ knows who's emitting frames
///   'm' — toggle monitor mode on this AGW connection (then BPQ pushes
///         every overheard frame as a 'U' record)
///   'M' — emit a UI frame (we use this to broadcast our beacon)
///
/// The bearer holds a dedicated AGW TCP connection (separate from the
/// per-stream connections <c>AgwOutboundTransport</c> opens for outbound
/// forwarding). It reads the connection's frames in a background loop,
/// filters for 'U' kind, attempts to parse the payload as a beacon, and
/// puts successful parses on a channel the daemon iterates.
///
/// Outgoing UI frames are addressed to a fixed broadcast destination
/// callsign (default <c>DAPPS</c>). The destination call doesn't gate
/// who hears the frame — every AGW client in monitor mode on the port
/// sees it — but it's a good convention so traffic captures can be
/// filtered in monitor tools later.
/// </summary>
public sealed class AgwUiDiscoveryBearer : IDiscoveryBearer
{
    public string Name => "agw";

    public const string DefaultBroadcastCall = "DAPPS";

    private readonly string _host;
    private readonly int _port;
    private readonly string _ourCallsign;
    private readonly byte _bpqPort;
    private readonly string _broadcastCall;
    private readonly ILogger _logger;

    private TcpClient? _tcp;
    private AgwFrameTransport? _framing;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;

    private readonly Channel<BeaconFrame> _incoming
        = Channel.CreateUnbounded<BeaconFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    public AgwUiDiscoveryBearer(
        string host, int port,
        string ourCallsign, int bpqPort,
        ILoggerFactory loggerFactory,
        string broadcastCall = DefaultBroadcastCall)
    {
        _host = host;
        _port = port;
        _ourCallsign = ourCallsign;
        if (bpqPort < 0 || bpqPort > 255)
            throw new ArgumentOutOfRangeException(nameof(bpqPort), "must fit in a single AGW port byte (0..255)");
        _bpqPort = (byte)bpqPort;
        _broadcastCall = broadcastCall;
        _logger = loggerFactory.CreateLogger<AgwUiDiscoveryBearer>();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_tcp is not null) return;

        var tcp = new TcpClient();
        await tcp.ConnectAsync(_host, _port, ct);
        var framing = new AgwFrameTransport(tcp.GetStream());

        // Register our callsign — without this, BPQ has no source to
        // attribute outgoing UI frames to and rejects the 'M' frame.
        await framing.WriteFrameAsync(
            new AgwFrame(0, 'X', 0, _ourCallsign, "", []),
            ct);
        var registerReply = await framing.ReadFrameAsync(ct);
        if (registerReply.Kind != 'X' || registerReply.Payload.Length != 1 || registerReply.Payload[0] != 0x01)
        {
            tcp.Dispose();
            throw new IOException(
                $"AGW register {_ourCallsign} for discovery failed (kind '{registerReply.Kind}')");
        }

        // Enable monitor mode on this connection — BPQ now mirrors every
        // overheard frame on the port to us as 'U' / 'T' / 'S' / 'I'.
        // The first byte of the toggle frame's payload is 1 (on).
        await framing.WriteFrameAsync(
            new AgwFrame(_bpqPort, 'm', 0, _ourCallsign, "", [1]),
            ct);

        _tcp = tcp;
        _framing = framing;

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

        _logger.LogInformation("AGW UI discovery monitoring on BPQ port {0} as {1}", _bpqPort, _ourCallsign);
    }

    public async Task AnnounceAsync(BeaconFrame beacon, CancellationToken ct)
    {
        if (_framing is null) throw new InvalidOperationException("AgwUiDiscoveryBearer not started");
        var payload = BeaconCodec.Encode(beacon);
        await _framing.WriteFrameAsync(
            new AgwFrame(_bpqPort, 'M', 0xF0, _ourCallsign, _broadcastCall, payload),
            ct);
    }

    public async IAsyncEnumerable<BeaconFrame> ListenAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var beacon in _incoming.Reader.ReadAllAsync(ct))
        {
            yield return beacon;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var framing = _framing ?? throw new InvalidOperationException();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                AgwFrame frame;
                try
                {
                    frame = await framing.ReadFrameAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AGW UI discovery read failed; ending loop");
                    break;
                }

                // 'U' = decoded inbound UI frame from BPQ. Other kinds
                // ('T', 'S', 'I') are fired during monitor mode too but
                // aren't UI frames — skip them.
                if (frame.Kind != 'U') continue;

                // Skip our own frames echoed back via monitor (they
                // arrive with our callsign as CallFrom).
                if (string.Equals(frame.CallFrom, _ourCallsign, StringComparison.OrdinalIgnoreCase))
                    continue;

                var hint = new AgwBearerHint(frame.Port);
                if (!BeaconCodec.TryParse(frame.Payload, hint, out var beacon) || beacon is null)
                    continue;

                await _incoming.Writer.WriteAsync(beacon, ct);
            }
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _readLoopCts?.Cancel();
            if (_readLoop is not null)
            {
                try { await _readLoop; } catch { /* expected on cancel */ }
            }
        }
        finally
        {
            _tcp?.Dispose();
            _readLoopCts?.Dispose();
        }
    }
}
