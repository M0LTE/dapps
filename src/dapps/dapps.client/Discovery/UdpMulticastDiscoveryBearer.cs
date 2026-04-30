using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace dapps.client.Discovery;

/// <summary>
/// Discovery over IP multicast. Each configured channel is one
/// multicast group endpoint ("239.42.42.42:1881"); the bearer joins
/// each group on its own socket. Datagram bodies are bare beacon
/// strings.
///
/// Useful for LAN dev / testing — every DAPPS instance on the same
/// subnet sees every other instance's beacons within seconds.
/// Operators opt in by adding a <c>udp</c> channel to the discovery
/// channels table.
/// </summary>
public sealed class UdpMulticastDiscoveryBearer : IDiscoveryBearer
{
    public string Name => "udp";

    private readonly string _ourCallsign;
    private readonly ILogger _logger;

    private readonly Dictionary<string, GroupBinding> _groups = new(StringComparer.Ordinal);

    private readonly Channel<ReceivedBeacon> _incoming
        = Channel.CreateUnbounded<ReceivedBeacon>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    public UdpMulticastDiscoveryBearer(string ourCallsign, ILoggerFactory loggerFactory)
    {
        _ourCallsign = ourCallsign;
        _logger = loggerFactory.CreateLogger<UdpMulticastDiscoveryBearer>();
    }

    public async Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct)
    {
        foreach (var ch in channels)
        {
            if (_groups.ContainsKey(ch.ChannelKey)) continue;

            IPEndPoint endpoint;
            try
            {
                endpoint = ParseGroup(ch.ChannelKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UDP multicast discovery: invalid channel-key '{0}' — skipping", ch.ChannelKey);
                continue;
            }

            var recv = new UdpClient();
            recv.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            recv.Client.Bind(new IPEndPoint(IPAddress.Any, endpoint.Port));
            recv.JoinMulticastGroup(endpoint.Address);

            var send = new UdpClient();
            send.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var binding = new GroupBinding(ch.ChannelKey, endpoint, send, recv, loopCts);
            _groups[ch.ChannelKey] = binding;
            binding.ReadLoop = Task.Run(() => ReadLoopAsync(binding, loopCts.Token), loopCts.Token);

            _logger.LogInformation("UDP multicast discovery joined {0} (channel-key '{1}')",
                endpoint, ch.ChannelKey);
        }
        await Task.CompletedTask;
    }

    public async Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct)
    {
        if (!_groups.TryGetValue(channelKey, out var binding))
        {
            throw new InvalidOperationException(
                $"No UDP multicast group bound for channel-key '{channelKey}'");
        }
        var bytes = BeaconCodec.Encode(beacon);
        await binding.Send.SendAsync(bytes, binding.Endpoint, ct);
    }

    public async IAsyncEnumerable<ReceivedBeacon> ListenAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var rb in _incoming.Reader.ReadAllAsync(ct))
        {
            yield return rb;
        }
    }

    private async Task ReadLoopAsync(GroupBinding binding, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult dgram;
                try
                {
                    dgram = await binding.Recv.ReceiveAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "UDP multicast receive failed on {0}; continuing", binding.Endpoint);
                    continue;
                }

                var hint = new UdpBearerHint($"{dgram.RemoteEndPoint.Address}:{dgram.RemoteEndPoint.Port}");
                if (!BeaconCodec.TryParse(dgram.Buffer, hint, out var beacon) || beacon is null)
                    continue;

                if (string.Equals(beacon.Callsign, _ourCallsign, StringComparison.OrdinalIgnoreCase))
                    continue; // self-echo via MulticastLoopback

                await _incoming.Writer.WriteAsync(new ReceivedBeacon(beacon, binding.ChannelKey), ct);
            }
        }
        finally
        {
            // We only complete the writer when ALL groups have stopped;
            // a single binding ending shouldn't shut listening down for
            // the others. The bearer's DisposeAsync handles final close.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _groups.Values)
        {
            try { b.Cts.Cancel(); } catch { /* best effort */ }
            if (b.ReadLoop is not null)
            {
                try { await b.ReadLoop; } catch { /* expected on cancel */ }
            }
            b.Send.Dispose();
            b.Recv.Dispose();
            b.Cts.Dispose();
        }
        _groups.Clear();
        _incoming.Writer.TryComplete();
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

    private sealed class GroupBinding(
        string channelKey,
        IPEndPoint endpoint,
        UdpClient send,
        UdpClient recv,
        CancellationTokenSource cts)
    {
        public string ChannelKey { get; } = channelKey;
        public IPEndPoint Endpoint { get; } = endpoint;
        public UdpClient Send { get; } = send;
        public UdpClient Recv { get; } = recv;
        public CancellationTokenSource Cts { get; } = cts;
        public Task? ReadLoop { get; set; }
    }
}
