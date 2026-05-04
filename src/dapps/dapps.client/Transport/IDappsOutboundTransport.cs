namespace dapps.client.Transport;

/// <summary>
/// A transport that originates a stream-style AX.25 connection from a local
/// callsign to a remote one through a packet node, and hands back a duplex
/// byte stream over which DAPPS protocol bytes can flow.
///
/// Today this is implemented over AGW. The shape is deliberately thin so
/// other implementations (RHP via WebSocket; future node interfaces) can
/// slot in alongside. The transport is responsible for:
///   - reaching the node
///   - identifying the local callsign as the source
///   - asking the node to connect to the remote callsign on a given bearer port
///   - returning a Stream over which raw bytes flow once connected
/// It is not responsible for any DAPPS-protocol semantics - that lives in
/// <see cref="DappsProtocolClient"/>.
/// </summary>
public interface IDappsOutboundTransport
{
    Task<IDappsConnection> ConnectAsync(
        string localCallsign,
        string remoteCallsign,
        int bearerPort,
        CancellationToken stoppingToken);
}

/// <summary>
/// A live stream-style connection to a remote callsign. Disposing closes the
/// underlying network resources.
/// </summary>
public interface IDappsConnection : IAsyncDisposable
{
    Stream Stream { get; }
}
