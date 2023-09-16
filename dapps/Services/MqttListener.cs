using dapps.Models;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Net.Sockets;

namespace dapps.Services;

internal class MqttListener : IHostedService
{
    private readonly ILogger<MqttListener> logger;
    private readonly ManagedMqttClientOptions options;
    private readonly IManagedMqttClient mqttClient;

    private const string mqttHost = "localhost";
    private const string bpqHost = "localhost";
    private const string bpqUser = "sysop";
    private const string bpqPassword = "rad10";

    private readonly ServiceConfig config;

    public MqttListener(ILogger<MqttListener> logger, IOptions<ServiceConfig> serviceOptions)
    {
        this.logger = logger;
        options = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .WithClientOptions(new MqttClientOptionsBuilder()
                .WithClientId(Guid.NewGuid().ToString())
                .WithTcpServer(mqttHost)
                .Build())
            .Build();

        mqttClient = new MqttFactory().CreateManagedMqttClient();

        config = serviceOptions.Value;

        if (string.IsNullOrWhiteSpace(config.Ssid))
        {
            throw new Exception("SSID not set");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        mqttClient.ApplicationMessageReceivedAsync += MqttClient_ApplicationMessageReceivedAsync;
        await mqttClient.StartAsync(options);
        await mqttClient.SubscribeAsync("dapps/apps/+/out/+");
    }

    /// <summary>
    /// We have received a request from some application on this local network to send a payload to a remote packet node
    /// </summary>
    /// <param name="arg"></param>
    /// <returns></returns>
    private async Task MqttClient_ApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        var topicParts = arg.ApplicationMessage.Topic.Split('/');

        var destAppName = topicParts[2];
        var destNode = topicParts[4];
        var payload = arg.ApplicationMessage.PayloadSegment.ToArray();

        logger.LogInformation("Received a request to send {bytes} bytes to app {app} on node {node}", payload.Length, destAppName, destNode);
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(bpqHost, 8011);
        using var stream = tcpClient.GetStream();
        using var streamReader = new StreamReader(stream);
        using var streamWriter = new StreamWriter(stream) { AutoFlush = true };
        await streamWriter.WriteAsync($"{bpqUser}\r{bpqPassword}\rBPQTERMTCP\r");
        streamReader.WaitToReceive("Connected to TelnetServer\r");
        logger.LogInformation("Connected to local node");
        await streamWriter.WriteAsync($"C {destNode}\r");
        var connectResult = streamReader.WaitToReceive("This is DAPPS\r");
        logger.LogInformation("Received handshake from remote node");

        var request = new Request
        {
            AppName = destAppName,
            Payload = payload,
            SourceCall = config.Ssid
        };

        var bytes = request.ToOnAirFormat();
        var lengthBytes = BitConverter.GetBytes((Int16)bytes.Length);
        logger.LogInformation("We have {length} bytes to send", bytes.Length);
        logger.LogInformation("lengthBytes is {length} bytes long", lengthBytes.Length);
        stream.Write(lengthBytes);
        stream.Write(bytes);
        logger.LogInformation("Sent request to far node, waiting...");
        streamReader.WaitToReceive("OK\r");
        logger.LogInformation("Received OK, disconnecting.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
