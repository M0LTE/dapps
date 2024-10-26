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
        await mqttClient.SubscribeAsync("dapps/out/+/+");
    }

    /// <summary>
    /// We have received a request from some application on this local network to send a payload to a remote packet node
    /// </summary>
    /// <param name="arg"></param>
    /// <returns></returns>
    private async Task MqttClient_ApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        try
        {
            var topicParts = arg.ApplicationMessage.Topic.Split('/');

            var destAppName = topicParts[2];
            var destNode = topicParts[3];
            var payload = arg.ApplicationMessage.PayloadSegment.ToArray();

            logger.LogInformation("Received a request to send {bytes} bytes to app {app} on node {node}", payload.Length, destAppName, destNode);
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(bpqHost, 8011);
            using var stream = tcpClient.GetStream();
            using var streamReader = new StreamReader(stream);
            using var streamWriter = new StreamWriter(stream) { AutoFlush = true };
            await streamWriter.WriteAsync($"{bpqUser}\r{bpqPassword}\rBPQTERMTCP\r");
            streamReader.WaitToReceive("Connected to TelnetServer\r");
            logger.LogInformation("Connected to local node, connecting to far node...");
            await streamWriter.WriteAsync($"C {destNode}\r");

            var connectResult = streamReader.WaitToReceive("\r");

            logger.LogInformation("connectResult: {connectResult}", connectResult); // NODE1:A0AAA} Connected to FARAPP:A0BBB-8


            if (!streamReader.WaitToReceive("\r", TimeSpan.FromSeconds(10), out var appConnectResult))
            {
                logger.LogInformation("Received '{received}', expected '*** Connected to DAPPS'", appConnectResult);
                return;
            }
            logger.LogInformation("appConnectResult: {appConnectResult}", appConnectResult); // 

            logger.LogInformation("Waiting to receive 'DAPPS>' from application...");
            if (!streamReader.WaitToReceive("DAPPS>\r", TimeSpan.FromSeconds(10), out var handshakeResult))
            {
                logger.LogInformation("Received '{received}'", handshakeResult);
                return;
            }
            logger.LogInformation("handshakeResult: {handshakeResult}", handshakeResult); // DAPPS>

            var request = new DappsMessage
            {
                Timestamp = DateTime.UtcNow, // TODO: get from header
                AppName = destAppName,
                Payload = payload,
                SourceCall = config.Ssid
            };

            byte[] onAirBytes = DappsMessageFormatter.ToOnAirFormat(request);
            byte[] lengthBytes = BitConverter.GetBytes((short)onAirBytes.Length);
            logger.LogInformation("We have {length} bytes to send", onAirBytes.Length);
            stream.Write(new[] { (byte)'d' }, 0, 1);
            stream.Write(lengthBytes);
            stream.Write(onAirBytes);
            logger.LogInformation("Sent request to far node, waiting...");
            streamReader.WaitToReceive("OK\r");
            logger.LogInformation("Received OK, disconnecting.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Caught exception");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
