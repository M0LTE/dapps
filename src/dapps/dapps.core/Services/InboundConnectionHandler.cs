using dapps.client;
using dapps.core.Models;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

namespace dapps.core.Services;

public class InboundConnectionHandler(
    TcpClient tcpClient,
    ILoggerFactory loggerFactory,
    Database database,
    MqttBrokerService mqtt,
    IOptionsMonitor<SystemOptions> options)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();

    // Inactivity timeout per spec — AX.25 T3 default is 3 min; matching that
    // keeps DAPPS sessions tearing down on roughly the same cadence as the
    // underlying link layer would on its own.
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The callsign of the connecting station, sent by BPQ as the
    /// first line of the TCP stream. Captured in <see cref="Handle"/> and
    /// stamped onto every message saved during this connection.</summary>
    private string sourceCallsign = "";

    internal async Task Handle(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Got connection from {0}, waiting for node to send callsign..", tcpClient.Client.RemoteEndPoint!.ToString());
            var stream = tcpClient.GetStream();

            try
            {
                sourceCallsign = (await Extensions.WithInactivityTimeout(
                    t => stream.ReadLine(t), InactivityTimeout, stoppingToken)).Trim();
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Inactivity timeout waiting for callsign, closing connection");
                return;
            }
            logger.LogInformation("Connection is from callsign {0}", sourceCallsign);

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
        
        var computedId = DappsMessage.ComputeHash(buffer, offer.Salt)[..7];

        if (computedId == id)
        {
            logger.LogInformation("Hash matches, saving and acknowledging message {0}", id);
            await database.SaveMessage(id, buffer, offer.Salt, offer.Destination, sourceCallsign, offer.AdditionalProperties, offer.Ttl);
            await database.DeleteOffer(id);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ack " + id + "\n"));

            // If the message is destined for an app on this node, push it to
            // the MQTT broker for any connected subscriber. The DB row stays
            // until explicit ack from the app, so disconnected subscribers
            // catch up via replay-on-subscribe.
            if (DestinationParser.IsLocal(offer.Destination, options.CurrentValue.Callsign))
            {
                var dbMessage = new DbMessage
                {
                    Id = id,
                    Payload = buffer,
                    Salt = offer.Salt,
                    Destination = offer.Destination,
                    SourceCallsign = sourceCallsign,
                    AdditionalProperties = offer.AdditionalProperties,
                    Ttl = offer.Ttl,
                };
                await mqtt.InjectInboundMessage(dbMessage);
            }
        }
        else
        {
            logger.LogWarning("Hash does not match - payload corrupt");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("bad " + id + "\n"));
        }
    }
}
