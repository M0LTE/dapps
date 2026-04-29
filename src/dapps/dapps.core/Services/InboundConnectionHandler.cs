using dapps.client;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace dapps.core.Services;

public class InboundConnectionHandler(TcpClient tcpClient, ILoggerFactory loggerFactory, Database database)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();

    // Inactivity timeout per spec — AX.25 T3 default is 3 min; matching that
    // keeps DAPPS sessions tearing down on roughly the same cadence as the
    // underlying link layer would on its own.
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(3);

    internal async Task Handle(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Got connection from {0}, waiting for node to send callsign..", tcpClient.Client.RemoteEndPoint!.ToString());
            var stream = tcpClient.GetStream();

            string callsign;
            try
            {
                callsign = await Extensions.WithInactivityTimeout(t => stream.ReadLine(t), InactivityTimeout, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Inactivity timeout waiting for callsign, closing connection");
                return;
            }
            logger.LogInformation("Connection is from callsign {0}", callsign);

            await stream.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
            await stream.FlushAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Waiting for command");

                string command;
                try
                {
                    command = await Extensions.WithInactivityTimeout(t => stream.ReadLine(t), InactivityTimeout, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Inactivity timeout waiting for command, closing connection");
                    return;
                }

                if (string.IsNullOrWhiteSpace(command))
                {
                    logger.LogInformation("Empty command, closing connection");
                    return;
                }

                var cmd = Interpret(command);

                if (cmd == null)
                {
                    logger.LogInformation("Unrecognised command {0}", command);
                    await stream.WriteUtf8AndFlush("eh?\n");
                    return;
                }
                else if (cmd == Command.Quit)
                {
                    logger.LogInformation("Client has asked to quit");
                    await stream.WriteUtf8AndFlush("bye\n");
                    return;
                }
                else if (cmd == Command.Help)
                {
                    await stream.WriteUtf8AndFlush("This is DAPPS. See https://github.com/M0LTE/dapps/blob/master/README.md for details.\n");
                }
                else if (cmd == Command.IHave)
                {
                    var parts = command.Split(' ');
                    if (parts.Length < 2)
                    {
                        logger.LogError("ihave command has wrong number of parts");
                        await stream.WriteUtf8AndFlush("error\n");
                    }
                    else
                    {
                        logger.LogInformation("Client is offering us message {0}", parts[1]);
                        await HandleMessageOffer(stream, command, stoppingToken);
                    }
                }
                else if (cmd == Command.Data)
                {
                    var parts = command.Split(' ');
                    if (parts.Length != 2)
                    {
                        logger.LogError("data command has wrong number of parts");
                        await stream.WriteUtf8AndFlush("error\n");
                    }
                    else
                    {
                        logger.LogInformation("Client is sending us data for message {0}", parts[1]);
                        await HandleData(stream, parts[1], stoppingToken);
                    }
                }
            }
        }
        finally
        {
            tcpClient.Dispose();
        }
    }

    private enum Command
    {
        Quit, 
        IHave,
        Data,
        Help
    }

    private static readonly string[] exitCommands = ["q", "bye", "quit", "exit"];
    private static readonly string[] helpCommands = ["info", "help"];

    private static Command? Interpret(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var command = input.Trim().ToLower();

        if (exitCommands.Contains(command))
        {
            return Command.Quit;
        }

        if (helpCommands.Contains(command))
        {
            return Command.Help;
        }

        var parts = command.Split(' ');

        if (parts[0] == "ihave")
        {
            return Command.IHave;
        }

        if (parts[0] == "data")
        {
            return Command.Data;
        }

        return null;
    }

    private async Task HandleMessageOffer(NetworkStream stream, string command, CancellationToken stoppingToken)
    {
        var parts = command.Split(' ');
        var id = parts[1];
        var kvps = parts[2..].Select(p => p.Split('=')).ToDictionary(item => item[0], item => item[1]);

        async Task ReplyWithError(string message)
        {
            logger.LogError(message);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("error " + id + "\n"), stoppingToken);
        }

        if (!kvps.TryGetValue("len", out var lenStr))
        {
            await ReplyWithError("Fatal: no length specified in message offer");
            return;
        }

        if (!int.TryParse(lenStr, out var len))
        {
            await ReplyWithError("Fatal: invalid length specified in message offer");
            return;
        }

        if (!kvps.TryGetValue("s", out var saltStr))
        {
            saltStr = "0";
        }

        if (!long.TryParse(saltStr, out var salt))
        {
            await ReplyWithError("Fatal: invalid salt specified in message offer");
            return;
        }

        if (!kvps.TryGetValue("fmt", out var fmt))
        {
            fmt = "p"; // absent fmt defaults to plain per spec
        }

        if (fmt != "d" && fmt != "p")
        {
            await ReplyWithError("Fatal: unknown format specified in message offer");
            return;
        }

        var hasClen = kvps.TryGetValue("clen", out var clenStr);
        if (fmt == "d" && !hasClen)
        {
            await ReplyWithError("Fatal: clen= is required when fmt=d");
            return;
        }
        if (fmt == "p" && hasClen)
        {
            await ReplyWithError("Fatal: clen= MUST NOT be supplied when fmt=p");
            return;
        }
        if (hasClen && (!int.TryParse(clenStr, out var clen) || clen < 0))
        {
            await ReplyWithError("Fatal: clen= must be a non-negative integer");
            return;
        }

        if (!kvps.TryGetValue("dst", out var dst))
        {
            await ReplyWithError("Fatal: no destination specified in message offer");
            return;
        }

        if (kvps.TryGetValue("chk", out var chk))
        {
            var chkError = ValidateChecksum(command, chk);
            if (chkError != null)
            {
                await ReplyWithError($"Fatal: {chkError}");
                return;
            }
        }

        logger.LogInformation("Accepting message {0} with params {1}", id, string.Join(", ", kvps.Select(item => $"{item.Key}={item.Value}")));

        await stream.WriteAsync(Encoding.UTF8.GetBytes("send " + id + "\n")); // this can send multiple space-separated IDs if we want

        await database.SaveOfferMetadata(id, kvps);
    }

    private const string ChkSuffixPrefix = " chk=";
    private const int ChkValueLength = 4;

    /// <summary>
    /// Validate the trailing `chk=NNNN` on an ihave line. Returns null on
    /// success, or a short error string. Per spec, chk MUST be the last KV
    /// on the line, immediately followed by `\n` (which the line reader has
    /// already stripped). Receivers MUST reject any line where `chk=` appears
    /// elsewhere.
    /// </summary>
    private static string? ValidateChecksum(string ihaveCommand, string providedChk)
    {
        var prefixIndex = ihaveCommand.LastIndexOf(ChkSuffixPrefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            return "chk parsed from KVs but not present in line as ' chk=' suffix";
        }

        if (prefixIndex + ChkSuffixPrefix.Length + ChkValueLength != ihaveCommand.Length)
        {
            return "chk MUST be the last KV on the line";
        }

        // Reject any earlier "chk=" occurrence (would also be interpreted by
        // a naïve KV split as the chk value).
        var firstChkIndex = ihaveCommand.IndexOf("chk=", StringComparison.Ordinal);
        if (firstChkIndex != prefixIndex + 1)
        {
            return "chk= must appear only as the last KV";
        }

        if (!ushort.TryParse(providedChk, System.Globalization.NumberStyles.HexNumber, null, out var providedValue))
        {
            return "chk value is not 4 hex characters";
        }

        var coveredBytes = Encoding.UTF8.GetBytes(ihaveCommand[..prefixIndex]);
        var expected = Crc16CcittFalse.Compute(coveredBytes);

        return expected == providedValue ? null : "chk mismatch (line corruption?)";
    }

    private async Task HandleData(NetworkStream stream, string id, CancellationToken stoppingToken)
    {
        var offer = await database.LoadOfferMetadata(id);

        var buffer = new byte[offer.Length];

        if (offer.Format == "d") // deflate
        {
            if (offer.CompressedLength is null)
            {
                // Shouldn't happen — we validate at offer time — but defend
                // against a corrupted DB row.
                logger.LogError("Offer {0} marked fmt=d but has no clen stored", id);
                await stream.WriteUtf8AndFlush("bad " + id + "\n");
                return;
            }

            logger.LogInformation("Waiting for {0} compressed bytes", offer.CompressedLength.Value);
            var compressed = new byte[offer.CompressedLength.Value];
            try
            {
                await Extensions.WithInactivityTimeout(t => stream.ReadExactlyAsync(compressed, t).AsTask(), InactivityTimeout, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Inactivity timeout waiting for compressed payload, closing");
                return;
            }
            logger.LogInformation("Received compressed bytes, decompressing");

            using var inputMs = new MemoryStream(compressed);
            using var decompressor = new DeflateStream(inputMs, CompressionMode.Decompress);
            using var outputMs = new MemoryStream(buffer.Length);
            await decompressor.CopyToAsync(outputMs, stoppingToken);

            if (outputMs.Length != buffer.Length)
            {
                logger.LogWarning("Decompressed length {0} does not match declared len={1}", outputMs.Length, buffer.Length);
                await stream.WriteUtf8AndFlush("bad " + id + "\n");
                return;
            }

            buffer = outputMs.ToArray();
        }
        else // fmt=p (or absent — default plain)
        {
            logger.LogInformation("Waiting for {0} uncompressed bytes", buffer.Length);
            try
            {
                await Extensions.WithInactivityTimeout(t => stream.ReadExactlyAsync(buffer, t).AsTask(), InactivityTimeout, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Inactivity timeout waiting for uncompressed payload, closing");
                return;
            }
            logger.LogInformation("Received uncompressed data");
        }

        var text = Encoding.UTF8.GetString(buffer);
        logger.LogInformation("Got message {0}", text);
        
        string hash;

        if (offer.Salt == null)
        {
            hash = ComputeHash(buffer, null);
        }
        else
        {
            hash = ComputeHash(buffer, offer.Salt);
        }

        if (hash[..7] == id)
        {
            logger.LogInformation("Hash matches, saving and acknowledging message {0}", id);
            await database.SaveMessage(id, buffer, offer.Salt, offer.Destination, offer.AdditionalProperties);
            await database.DeleteOffer(id);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ack " + id + "\n"));
        }
        else
        {
            logger.LogWarning("Hash does not match - payload corrupt");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("bad " + id + "\n"));
        }
    }

    private static string ComputeHash(byte[] data, long? salt)
    {
        byte[] toHash;
        if (salt != null)
        {
            var saltBytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(saltBytes, salt.Value);
            toHash = [.. saltBytes, .. data];
        }
        else
        {
            toHash = data;
        }

        byte[] hashBytes = SHA1.HashData(toHash);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
