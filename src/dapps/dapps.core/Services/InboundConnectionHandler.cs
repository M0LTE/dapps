using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using dapps.core.Models;

namespace dapps.core.Services;

public class InboundConnectionHandler(TcpClient tcpClient, ILoggerFactory loggerFactory, Database database)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<InboundConnectionHandler>();

    internal async Task Handle(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Got connection from {0}", tcpClient.Client.RemoteEndPoint!.ToString());
            var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };

            var callsign = await reader.ReadLineAsync(stoppingToken);
            logger.LogInformation("Got callsign {0}", callsign);

            await writer.WriteLineAsync("DAPPSv1>");

            while (!stoppingToken.IsCancellationRequested)
            {
                var command = await reader.ReadLineAsync(stoppingToken);

                if (command == "q") // quit
                {
                    logger.LogInformation("Client has asked to quit");
                    return;
                }
                else if (command!.StartsWith("ihave "))
                {
                    var parts = command.Split(' ');
                    logger.LogInformation("Client is offering us message {0}", parts[1]);
                    await HandleMessageOffer(stream, parts[1], parts[2..].Select(p => p.Split('=')).ToDictionary(item => item[0], item => item[1]), stoppingToken);
                }
                else if (command!.StartsWith("data ")) // there's a bug in here somewhere
                {
                    var parts = command.Split(' ');
                    logger.LogInformation("Client is sending us data for message {0}", parts[1]);
                    await HandleData(stream, parts[1], stoppingToken);
                }
            }
        }
        finally
        {
            tcpClient.Dispose();
        }
    }

    private async Task HandleMessageOffer(NetworkStream stream, string id, Dictionary<string, string> kvps, CancellationToken stoppingToken)
    {
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

        if (kvps.TryGetValue("chk", out var chk))
        {
            //TODO: implement checksum verification of the offer line
        }

        logger.LogInformation("Accepting message {0} with params {1}", id, string.Join(", ", kvps.Select(item => $"{item.Key}={item.Value}")));

        await stream.WriteAsync(Encoding.UTF8.GetBytes("send " + id + "\n")); // this can send multiple space-separated IDs if we want

        await database.SaveOfferMetadata(id, kvps);
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
        }
        else if (offer.Format == "p") // plain
        {
            //await stream.WriteAsync(Encoding.UTF8.GetBytes("ok " + id + "\n"), stoppingToken);
            //await stream.FlushAsync();

            logger.LogInformation("Waiting for uncompressed data");

            //BUG: this blocks despite the client having sent data. Even just the first byte. It's as if the data wasn't sent. A 10ms delay in the client is enough to fix it.
            //await stream.ReadExactlyAsync(buffer, stoppingToken);
            byte[] nothing = new byte[0];
            stream.Read(nothing, 0, 0);

            for (int i = 0; i < offer.Length; i++)
            {
                buffer[i] = (byte)stream.ReadByte();
            }
            
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
            await database.SaveMessage(id, buffer);
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

    private async Task HandleJson(NetworkStream stream)
    {
        IAsyncEnumerable<JsonNode?> enumerable = JsonSerializer.DeserializeAsyncEnumerable<JsonNode?>(stream);
        await foreach (JsonNode? obj in enumerable)
        {
            logger.LogInformation("Got object {0}", obj);
            await HandleObject(obj);
        }
    }

    private async Task HandleCompressedJson(NetworkStream stream)
    {
        using var decompressor = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
        IAsyncEnumerable<JsonNode?> enumerable = JsonSerializer.DeserializeAsyncEnumerable<JsonNode?>(decompressor);
        await foreach (JsonNode? obj in enumerable)
        {
            logger.LogInformation("Got object {0}", obj);
            await HandleObject(obj);
        }
    }

    private async Task HandleObject(JsonNode? obj)
    {
        if (obj["type"] != null && obj["type"]!.GetValue<string>() == "Msg")
        {
            var deserialised = JsonSerializer.Deserialize<DappsMessage>(obj);
        }
    }
}
