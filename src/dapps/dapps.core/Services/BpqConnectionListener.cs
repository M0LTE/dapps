using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace dapps.core.Services;

public class BpqConnectionListener(BpqConnectionHandlerFactory bpqConnectionHandlerFactory, ILogger<BpqConnectionListener> logger) : IHostedService
{
    private readonly CancellationTokenSource stoppingTokenSource = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(StartListener, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task StartListener()
    {
        var listener = new TcpListener(IPAddress.Any, 11000);
        listener.Start();

        while (!stoppingTokenSource.IsCancellationRequested)
        {
            logger.LogInformation("Waiting for connection on port {0}", listener.LocalEndpoint!.ToString());
            var client = await listener.AcceptTcpClientAsync(stoppingTokenSource.Token);
            var bpqConnectionHandler = bpqConnectionHandlerFactory.Create(client);
            _ = Task.Run(() => bpqConnectionHandler.Handle(stoppingTokenSource.Token));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stoppingTokenSource.CancelAsync();
    }
}

public class BpqConnectionHandlerFactory(ILoggerFactory loggerFactory)
{
    internal BpqConnectionHandler Create(TcpClient tcpClient)
    {
        return new BpqConnectionHandler(tcpClient, loggerFactory);
    }
}

public class BpqConnectionHandler(TcpClient tcpClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<BpqConnectionHandler>();

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
            }
        }
        finally
        {
            tcpClient.Dispose();
        }
    }

    private async Task HandleMessageOffer(NetworkStream stream, string id, Dictionary<string, string> kvps, CancellationToken stoppingToken)
    {
        logger.LogInformation("Accepting message {0} with params {1}", id, string.Join(", ", kvps.Select(item => $"{item.Key}={item.Value}")));

        // for now let's just accept all messages
        await stream.WriteAsync(Encoding.UTF8.GetBytes("send " + id + "\n"));

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

        if (!kvps.TryGetValue("fmt", out var fmt))
        {
            logger.LogWarning("No format specified in message offer, assuming plain");
            fmt = "p";
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

        var buffer = new byte[len];

        if (fmt == "d") // deflate
        {
            using var decompressor = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
            await decompressor.ReadExactlyAsync(buffer, stoppingToken);
        }
        else if (fmt == "p") // plain
        {
            await stream.ReadExactlyAsync(buffer, stoppingToken);
        }
        else
        {
            await ReplyWithError($"Fatal: unknown format {fmt}");
            return;
        }

        var text = Encoding.UTF8.GetString(buffer);
        logger.LogInformation("Got message {0}", text);

        string hash;

        if (ts == 0)
        {
            hash = ComputeHash(buffer, null);
        }
        else
        {
            hash = ComputeHash(buffer, ts);
        }

        if (hash[..7] == id)
        {
            logger.LogInformation("Hash matches, acknowledging message {0}", id);
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

readonly record struct DappsMessage
{
    public DateTime Ttl { get; init; }
    public string SourceNode { get; init; }
    public string SourceCall { get; init; }
    public string DestNode { get; init; }
    public string DestTopic { get; init; }
    public JsonObject Data { get; init; }
}

public static class Extensions
{
    public static Task<string> ReadToCr(this StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var c = reader.Read();

            if (c == -1)
            {
                break;
            }

            if (c == '\r')
            {
                break;
            }

            sb.Append((char)c);
        }
        return Task.FromResult(sb.ToString());
    }
}