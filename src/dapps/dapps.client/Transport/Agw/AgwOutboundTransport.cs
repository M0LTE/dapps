using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace dapps.client.Transport.Agw;

/// <summary>
/// AGW-protocol implementation of <see cref="IDappsOutboundTransport"/>.
/// Each call to <see cref="ConnectAsync"/> opens a fresh TCP socket to the
/// node, asks for an AX.25 connection, and returns a Stream once the remote
/// confirms.
/// </summary>
public sealed class AgwOutboundTransport(string host, int port, ILoggerFactory loggerFactory)
    : IDappsOutboundTransport
{
    private readonly ILogger logger = loggerFactory.CreateLogger<AgwOutboundTransport>();

    public async Task<IDappsConnection> ConnectAsync(
        string localCallsign,
        string remoteCallsign,
        int bpqPortNumber,
        CancellationToken stoppingToken)
    {
        if (bpqPortNumber < 0 || bpqPortNumber > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(bpqPortNumber), "AGW port byte must fit in a single octet (0..255)");
        }
        var portByte = (byte)bpqPortNumber;

        var tcp = new TcpClient();
        try
        {
            logger.LogInformation("AGW: connecting to {host}:{port}", host, port);
            await tcp.ConnectAsync(host, port, stoppingToken);
            var ns = tcp.GetStream();
            var framing = new AgwFrameTransport(ns);

            // Register our local callsign first. Without this BPQ has no
            // valid source for the SABM and emits frames with a blank src
            // field — the remote ignores them and we time out with
            // RETRYOUT. (linbpq's own AGW two-instance test does this for
            // the same reason.)
            logger.LogDebug("AGW: registering {local}", localCallsign);
            await framing.WriteFrameAsync(
                new AgwFrame(0, 'X', 0, localCallsign, "", []),
                stoppingToken);
            var registerReply = await framing.ReadFrameAsync(stoppingToken);
            if (registerReply.Kind != 'X' || registerReply.Payload.Length != 1 || registerReply.Payload[0] != 0x01)
            {
                throw new IOException(
                    $"AGW register {localCallsign} failed (kind '{registerReply.Kind}', payload [{string.Join(',', registerReply.Payload)}])");
            }

            logger.LogInformation("AGW: requesting {local}->{remote} on port {p}", localCallsign, remoteCallsign, portByte);
            await framing.WriteFrameAsync(
                new AgwFrame(portByte, 'C', 0xF0, localCallsign, remoteCallsign, []),
                stoppingToken);

            // Wait for the connect to confirm. BPQ replies with 'C' echoing
            // the request on success, or 'd' on failure. Other frames may
            // arrive ahead of the answer (e.g. 'G' if we'd queried, port
            // descriptors, monitor traffic if turned on); skip them.
            while (true)
            {
                var frame = await framing.ReadFrameAsync(stoppingToken);

                // BPQ's 'C' confirmation comes back with callfrom/callto
                // swapped relative to the request — the remote (the one we
                // dialed) is now the FROM and we're the TO. Accept either
                // orientation defensively, since the spec is ambiguous and
                // some clients don't swap.
                if (frame.Kind == 'C')
                {
                    var matchesAsConfirm =
                        string.Equals(frame.CallFrom, remoteCallsign, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(frame.CallTo, localCallsign, StringComparison.OrdinalIgnoreCase);
                    var matchesAsEcho =
                        string.Equals(frame.CallFrom, localCallsign, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(frame.CallTo, remoteCallsign, StringComparison.OrdinalIgnoreCase);

                    if (matchesAsConfirm || matchesAsEcho)
                    {
                        logger.LogInformation("AGW: connect confirmed");
                        var sessionStream = new AgwSessionStream(framing, portByte, localCallsign, remoteCallsign, logger);
                        return new AgwConnection(tcp, sessionStream);
                    }
                }

                if (frame.Kind == 'd')
                {
                    var msg = Encoding.ASCII.GetString(frame.Payload).TrimEnd('\0', '\r', '\n');
                    throw new IOException($"AGW connect to {remoteCallsign} failed: {msg}");
                }

                logger.LogDebug("AGW: ignoring frame kind '{0}' while waiting for connect confirmation", frame.Kind);
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private sealed class AgwConnection(TcpClient tcp, Stream sessionStream) : IDappsConnection
    {
        public Stream Stream => sessionStream;

        public ValueTask DisposeAsync()
        {
            sessionStream.Dispose();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
