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

    private const string ExpectedPrompt = "DAPPSv1>\n";
    private const int PromptScanCapBytes = 256;

    /// <summary>
    /// Reads from the stream until either the DAPPSv1 prompt is seen or we
    /// exceed PromptScanCapBytes (which is enough to absorb a typical noisy
    /// connect-banner from a misbehaving node without becoming a DoS sink).
    /// </summary>
    public async Task<bool> ReadInitialPromptAsync(CancellationToken ct)
    {
        var prompt = Encoding.UTF8.GetBytes(ExpectedPrompt);
        var seen = new List<byte>();
        var oneByte = new byte[1];

        while (seen.Count < PromptScanCapBytes)
        {
            var n = await stream.ReadAsync(oneByte, ct);
            if (n == 0)
            {
                logger.LogWarning("EOF before DAPPSv1> prompt (got {0} bytes)", seen.Count);
                return false;
            }
            seen.Add(oneByte[0]);

            if (seen.Count >= prompt.Length
                && seen.GetRange(seen.Count - prompt.Length, prompt.Length).SequenceEqual(prompt))
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
        CancellationToken ct)
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

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var buffer = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(oneByte, ct);
            if (n == 0) break;
            if (oneByte[0] == '\n') break;
            buffer.Add(oneByte[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
