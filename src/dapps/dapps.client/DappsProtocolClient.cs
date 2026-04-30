using System.Text;
using Microsoft.Extensions.Logging;

namespace dapps.client;

/// <summary>
/// Speaks the DAPPSv1 protocol over a duplex byte stream — agnostic of how
/// that stream is plumbed. Pair with any <see cref="Transport.IDappsOutboundTransport"/>.
///
/// Today this is the sender side only: read the initial prompt, offer a
/// message, send its payload. Receiver-side (`ihave` parsing, `chk`
/// validation) lives in dapps.core's IHaveValidator.
/// </summary>
public class DappsProtocolClient(Stream stream, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<DappsProtocolClient>();

    private const string PromptText = "DAPPSv1>";
    private const int PromptScanCapBytes = 256;

    /// <summary>Per-read inactivity timeout. Mirrors the receiver-side
    /// budget in <c>InboundConnectionHandler</c> (3 minutes, matching the
    /// AX.25 T3 default) so a hung peer can't wedge a forwarder run
    /// indefinitely. Plan A3.</summary>
    public static TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Reads from the stream until either the DAPPSv1 prompt is seen or we
    /// exceed PromptScanCapBytes (which is enough to absorb a typical noisy
    /// connect-banner from a misbehaving node without becoming a DoS sink).
    ///
    /// We match on <c>"DAPPSv1>"</c> followed by *any* line terminator
    /// (<c>\n</c>, <c>\r</c>, or <c>\r\n</c>) — BPQ's Telnet driver, when
    /// bridging an inbound L2 session via Apps Interface, rewrites LF → CR
    /// in the app-to-user direction (apps-interface.md "App → user"
    /// section). Strict <c>\n</c>-only matching would hang every time
    /// dapps is reached over that bridge.
    /// </summary>
    public async Task<bool> ReadInitialPromptAsync(CancellationToken ct)
    {
        var promptBytes = Encoding.UTF8.GetBytes(PromptText);
        var seen = new List<byte>();
        var oneByte = new byte[1];
        var promptSeen = false;

        while (seen.Count < PromptScanCapBytes)
        {
            var n = await ReadWithTimeoutAsync(oneByte, ct);
            if (n == 0)
            {
                logger.LogWarning("EOF before DAPPSv1> prompt (got {0} bytes)", seen.Count);
                return false;
            }
            seen.Add(oneByte[0]);

            if (!promptSeen
                && seen.Count >= promptBytes.Length
                && seen.GetRange(seen.Count - promptBytes.Length, promptBytes.Length).SequenceEqual(promptBytes))
            {
                promptSeen = true;
                continue;
            }

            if (promptSeen && (oneByte[0] == (byte)'\n' || oneByte[0] == (byte)'\r'))
            {
                return true;
            }
        }

        logger.LogWarning("DAPPSv1> prompt not seen in first {0} bytes", PromptScanCapBytes);
        return false;
    }

    /// <summary>
    /// Sends an `ihave` line and waits for `send &lt;id&gt;`. Returns true on
    /// acceptance. Today only fmt=p (plain) is supported on the sender
    /// side; fmt=d would need clen to be threaded through.
    /// </summary>
    public async Task<bool> OfferMessageAsync(
        string id,
        long? salt,
        DappsMessage.MessageFormat format,
        string destination,
        int length,
        CancellationToken ct,
        int? ttl = null)
    {
        if (format != DappsMessage.MessageFormat.Plain)
        {
            throw new NotImplementedException("Deflate format not yet wired through outbound");
        }

        var sb = new StringBuilder($"ihave {id} len={length} fmt=p dst={destination}");
        if (salt.HasValue)
        {
            sb.Append($" s={salt}");
        }
        if (ttl.HasValue)
        {
            sb.Append($" ttl={ttl.Value}");
        }
        sb.Append('\n');

        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
        await stream.FlushAsync(ct);

        var line = await ReadLineAsync(ct);
        if (line == $"send {id}")
        {
            return true;
        }

        logger.LogError("Expected 'send {0}', got '{1}'", id, line);
        return false;
    }

    /// <summary>
    /// Sends `data &lt;id&gt;` followed by the raw payload bytes, then waits
    /// for `ack &lt;id&gt;` (success) or `bad &lt;id&gt;` (corrupt — far
    /// end's hash didn't match).
    /// </summary>
    public async Task<bool> SendMessageAsync(string id, byte[] payload, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data {id}\n"), ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);

        var line = await ReadLineAsync(ct);
        if (line == $"ack {id}")
        {
            return true;
        }
        if (line == $"bad {id}")
        {
            logger.LogError("Remote NAKed message {0} — payload hash mismatch", id);
            return false;
        }

        logger.LogError("Expected 'ack/bad {0}', got '{1}'", id, line);
        return false;
    }

    /// <summary>
    /// Reads a line terminated by <c>\n</c>, <c>\r</c>, or <c>\r\n</c>.
    /// Leading line terminators are skipped (so a stranded <c>\n</c>
    /// after a <c>\r\n</c> sequence on the previous call doesn't yield
    /// a phantom empty line). Empty lines from the peer are not part of
    /// the DAPPSv1 protocol so this is safe.
    ///
    /// BPQ's Telnet driver rewrites line endings in the app→user
    /// direction (LF → CR with the default text-mode line discipline,
    /// per apps-interface.md), so accepting either form is necessary
    /// when dapps is reached via the BPQ APPLICATION+ATTACH bridge.
    /// </summary>
    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var buffer = new List<byte>();
        var oneByte = new byte[1];
        var sawContent = false;
        while (true)
        {
            var n = await ReadWithTimeoutAsync(oneByte, ct);
            if (n == 0) break;
            if (oneByte[0] == (byte)'\n' || oneByte[0] == (byte)'\r')
            {
                if (!sawContent) continue;   // skip leading terminator(s)
                break;
            }
            sawContent = true;
            buffer.Add(oneByte[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads with a per-call inactivity timeout layered on top of the
    /// caller's cancellation token. Surfaces an explicit
    /// <see cref="TimeoutException"/> when the peer goes silent —
    /// callers (e.g. <c>OutboundMessageManager</c>) catch and log,
    /// then move on to the next message rather than hanging the run.
    /// </summary>
    private async ValueTask<int> ReadWithTimeoutAsync(Memory<byte> buffer, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(InactivityTimeout);
        try
        {
            return await stream.ReadAsync(buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"DAPPS sender: no data from peer within {InactivityTimeout.TotalSeconds:F0}s");
        }
    }
}
