using System.IO.Compression;
using System.Text;
using System.Text.Json;
using dapps.client;
using dapps.client.Backhaul;

namespace dapps.core.Services;

/// <summary>
/// Receiver-side DAPPSv1 session reader. Bearer-neutral: takes a
/// duplex byte <see cref="Stream"/> already-connected to a peer and
/// the peer's callsign already-determined (each bearer figures the
/// callsign out its own way — AGW reads it off the inbound 'C' frame's
/// CallFrom field; the legacy Apps-Interface bearer read it from the
/// first line of the bridged TCP socket). Owns the
/// `prompt` / `ihave` / `data` correlation and the on-the-wire ack
/// contract. Once a payload is received and hash-validated, the
/// completed message is handed off to <see cref="IBackhaulInbox"/> —
/// where DAPPS-level concerns (queue persistence, MQTT injection,
/// future forwarding decisions) live, decoupled from the bearer.
/// </summary>
public class InboundConnectionHandler(
    Stream stream,
    string sourceCallsign,
    ILoggerFactory loggerFactory,
    Database database,
    IBackhaulInbox inbox)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();

    // Inactivity timeout per spec — AX.25 T3 default is 3 min; matching that
    // keeps DAPPS sessions tearing down on roughly the same cadence as the
    // underlying link layer would on its own.
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(3);

    public async Task Handle(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Inbound session from {0}", sourceCallsign);

            await stream.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n"), stoppingToken);
            await stream.FlushAsync(stoppingToken);

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
            await stream.DisposeAsync();
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

    private async Task HandleMessageOffer(Stream stream, string command, CancellationToken stoppingToken)
    {
        var result = IHaveValidator.Validate(command);
        if (!result.IsValid)
        {
            logger.LogError("Rejecting offer: {0}", result.Error);
            var idForReply = result.Id ?? "??";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"error {idForReply}\n"), stoppingToken);
            return;
        }

        var offer = result.Offer!;
        logger.LogInformation("Accepting message {0} (len={1}, fmt={2}, dst={3})", offer.Id, offer.Length, offer.Format, offer.Destination);

        await stream.WriteAsync(Encoding.UTF8.GetBytes($"send {offer.Id}\n"));
        await database.SaveOffer(offer);
    }

    private async Task HandleData(Stream stream, string id, CancellationToken stoppingToken)
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
        
        var computedId = DappsMessage.ComputeHash(buffer, offer.Salt)[..7];

        if (computedId == id)
        {
            logger.LogInformation("Hash matches, handing message {0} to the inbox", id);

            // Rehydrate the offer's stored AdditionalProperties JSON back into
            // a header dict for the bearer-neutral inbox. Empty/missing →
            // null, which the inbox treats as no headers.
            IReadOnlyDictionary<string, string>? headers = null;
            if (!string.IsNullOrWhiteSpace(offer.AdditionalProperties)
                && offer.AdditionalProperties != "{}")
            {
                try
                {
                    headers = JsonSerializer.Deserialize<Dictionary<string, string>>(offer.AdditionalProperties);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Could not parse stored offer headers for {0}; dropping", id);
                }
            }

            var backhaulMessage = new BackhaulMessage(
                Id: id,
                Destination: offer.Destination,
                Salt: offer.Salt,
                Ttl: offer.Ttl,
                Payload: buffer,
                Headers: headers);

            await inbox.DeliverAsync(backhaulMessage, sourceCallsign, stoppingToken);
            await database.DeleteOffer(id);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ack " + id + "\n"));
        }
        else
        {
            logger.LogWarning("Hash does not match - payload corrupt");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("bad " + id + "\n"));
        }
    }
}
