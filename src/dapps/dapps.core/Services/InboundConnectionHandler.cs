using dapps.client;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace dapps.core.Services;

public class InboundConnectionHandler(TcpClient tcpClient, ILoggerFactory loggerFactory, Database database)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();
    
    internal async Task Handle(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Got connection from {0}, waiting for node to send callsign..", tcpClient.Client.RemoteEndPoint!.ToString());
            var stream = tcpClient.GetStream();

            var callsign = await stream.ReadLine(stoppingToken);
            logger.LogInformation("Connection is from callsign {0}", callsign);

            await stream.WriteAsync(Encoding.UTF8.GetBytes("DAPPSv1>\n"));
            await stream.FlushAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Waiting for command");
                var command = await stream.ReadLine(stoppingToken);

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

        if (!kvps.TryGetValue("ts", out var tsStr))
        {
            logger.LogWarning("No timestamp specified in message offer, no dupe check");
            tsStr = "0";
        }

        if (!long.TryParse(tsStr, out var ts))
        {
            await ReplyWithError("Fatal: invalid timestamp specified in message offer");
            return;
        }

        if (!kvps.TryGetValue("fmt", out var fmt))
        {
            logger.LogWarning("No format specified in message offer, assuming plain");
            fmt = "p";
        }

        if (fmt != "d" && fmt != "p")
        {
            await ReplyWithError("Fatal: unknown format specified in message offer");
            return;
        }

        if (!kvps.TryGetValue("dst", out var dst))
        {
            await ReplyWithError("Fatal: no destination specified in message offer");
            return;
        }

        if (kvps.TryGetValue("chk", out var chk))
        {
            if (chk != ComputeChecksum(command, chk))
            {
                await ReplyWithError("Fatal: corrupt command");
                return;
            }
        }

        logger.LogInformation("Accepting message {0} with params {1}", id, string.Join(", ", kvps.Select(item => $"{item.Key}={item.Value}")));

        await stream.WriteAsync(Encoding.UTF8.GetBytes("send " + id + "\n")); // this can send multiple space-separated IDs if we want

        await database.SaveOfferMetadata(id, kvps);
    }

    private static string ComputeChecksum(string ihaveCommand, string chk)
    {
        // remove the checksum part
        ///TODO: Do this properly- the chk command could be in the middle of the string and double-spaces could be present
        ihaveCommand = ihaveCommand.Replace("chk=" + chk, "").Trim();

        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(ihaveCommand));
        var sum = BitConverter.ToString(hash).Replace("-", "").ToLower()[..2];

        return sum;
    }

    private async Task HandleData(NetworkStream stream, string id, CancellationToken stoppingToken)
    { 
        var offer = await database.LoadOfferMetadata(id);

        var buffer = new byte[offer.Length];

        if (offer.Format == "d") // deflate
        {
            logger.LogInformation("Waiting for deflated data");
            using var decompressor = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
            await decompressor.ReadExactlyAsync(buffer, stoppingToken);
            logger.LogInformation("Received deflated data");
        }
        else if (offer.Format == "p") // plain
        {
            logger.LogInformation("Waiting for uncompressed data");
            await stream.ReadExactlyAsync(buffer, stoppingToken);
            logger.LogInformation("Received uncompressed data");
        }

        var text = Encoding.UTF8.GetString(buffer);
        logger.LogInformation("Got message {0}", text);
        
        string hash;

        if (offer.Timestamp == null)
        {
            logger.LogWarning("No timestamp specified in message offer, no dupe check");
            hash = ComputeHash(buffer, null);
        }
        else
        {
            hash = ComputeHash(buffer, offer.Timestamp);
        }

        if (hash[..7] == id)
        {
            logger.LogInformation("Hash matches, saving and acknowledging message {0}", id);
            await database.SaveMessage(id, buffer, offer.Timestamp, offer.Destination, offer.AdditionalProperties);
            await database.DeleteOffer(id);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ack " + id + "\n"));
        }
        else
        {
            logger.LogWarning("Hash does not match - payload corrupt");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("bad " + id + "\n"));
        }
    }

    private static string ComputeHash(byte[] data, long? timestamp)
    {
        byte[] toHash;
        if (timestamp != null)
        {
            var tsBytes = BitConverter.GetBytes(timestamp.Value);
            toHash = [.. tsBytes, .. data];
        }
        else
        {
            toHash = data;
        }

        var sha = SHA1.Create();
        byte[] hashBytes = sha.ComputeHash(toHash);
        var str = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        return str;
    }
}
