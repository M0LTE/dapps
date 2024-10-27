using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
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
                else if (command == "ja") // JSON array
                {
                    logger.LogInformation("Client is giving us an array of JSON objects");
                    await HandleJson(stream);
                }
                else if (command == "cja") // compressed JSON array
                {
                    logger.LogInformation("Client is giving us a compressed array of JSON objects");
                    await HandleCompressedJson(stream);
                }
            }
        }
        finally
        {
            tcpClient.Dispose();
        }
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