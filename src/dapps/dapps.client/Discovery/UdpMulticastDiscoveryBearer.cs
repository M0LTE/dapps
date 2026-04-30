using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace dapps.client.Discovery;

/// <summary>
/// Discovery over IP multicast. Sends and receives beacons on a
/// configurable group (e.g. <c>239.42.42.42:1881</c>). Useful for
/// LAN dev / testing — every DAPPS instance on the same subnet sees
/// every other instance's beacons within seconds, without configuring
/// neighbour tables by hand.
///
/// Off by default. Operators opt in by setting
/// <c>SystemOptions.MulticastGroup</c> ("host:port"); doing so is a
/// statement of intent ("I want to discover peers on this segment"),
/// which avoids stomping on other multicast traffic in shared LANs.
///
/// The receive socket joins the group on the loopback or default
/// interface and rejects datagrams whose body parses as our own beacon
/// (callsign-match) — a node mustn't list itself as a neighbour.
/// </summary>
public sealed class UdpMulticastDiscoveryBearer : IDiscoveryBearer
{
    public string Name => "udp-multicast";

    private readonly IPEndPoint _group;
    private readonly string _ourCallsign;
    private readonly ILogger _logger;

    private UdpClient? _send;
    private UdpClient? _recv;
    private readonly Channel<BeaconFrame> _incoming
        = Channel.CreateUnbounded<BeaconFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;

    public UdpMulticastDiscoveryBearer(string groupEndpoint, string ourCallsign, ILoggerFactory loggerFactory)
    {
        _group = ParseGroup(groupEndpoint);
        _ourCallsign = ourCallsign;
        _logger = loggerFactory.CreateLogger<UdpMulticastDiscoveryBearer>();
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_recv is not null) return Task.CompletedTask;

        // Receiver: bound to ANY:port, joined to the group.
        _recv = new UdpClient();
        _recv.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _recv.Client.Bind(new IPEndPoint(IPAddress.Any, _group.Port));
        _recv.JoinMulticastGroup(_group.Address);

        // Sender: ephemeral source port, MulticastLoopback on so a single-
        // host test setup can hear its own peers via this same daemon
        // (different DAPPS instance on the same box still shares the
        // loopback — and we callsign-filter our own).
        _send = new UdpClient();
        _send.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

        _logger.LogInformation("UDP multicast discovery joined {0}", _group);
        return Task.CompletedTask;
    }

    public async Task AnnounceAsync(BeaconFrame beacon, CancellationToken ct)
    {
        if (_send is null) throw new InvalidOperationException("UdpMulticastDiscoveryBearer not started");
        var bytes = BeaconCodec.Encode(beacon);
        await _send.SendAsync(bytes, _group, ct);
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
        var recv = _recv ?? throw new InvalidOperationException();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult dgram;
                try
                {
                    dgram = await recv.ReceiveAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UDP multicast receive failed; continuing");
                    continue;
                }

                var hint = new UdpBearerHint($"{dgram.RemoteEndPoint.Address}:{dgram.RemoteEndPoint.Port}");
                if (!BeaconCodec.TryParse(dgram.Buffer, hint, out var beacon) || beacon is null)
                {
                    continue; // not a DAPPS beacon, or malformed — drop
                }

                if (string.Equals(beacon.Callsign, _ourCallsign, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // our own beacon looped back; ignore
                }

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
            _recv?.Dispose();
            _send?.Dispose();
            _readLoopCts?.Dispose();
        }
    }

    private static IPEndPoint ParseGroup(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("multicast group must be host:port", nameof(raw));
        var colon = raw.LastIndexOf(':');
        if (colon <= 0 || colon == raw.Length - 1)
            throw new FormatException($"expected host:port, got '{raw}'");
        if (!IPAddress.TryParse(raw[..colon], out var addr))
            throw new FormatException($"'{raw[..colon]}' is not a valid IP literal");
        if (!int.TryParse(raw[(colon + 1)..], out var port) || port is < 1 or > 65535)
            throw new FormatException($"port must be 1..65535, got '{raw[(colon + 1)..]}'");
        return new IPEndPoint(addr, port);
    }
}
