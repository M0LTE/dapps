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

                if (frame.Kind == 'C'
                    && string.Equals(frame.CallFrom, localCallsign, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(frame.CallTo, remoteCallsign, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("AGW: connect confirmed");
                    var sessionStream = new AgwSessionStream(framing, portByte, localCallsign, remoteCallsign, logger);
                    return new AgwConnection(tcp, sessionStream);
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
