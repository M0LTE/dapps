using dapps.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Intrinsics.Arm;
using System.Text;

namespace dapps.Services;

/// <summary>
/// Service that sits on a TCP port and accepts application connections from BPQ.
/// </summary>
public class BpqApplicationService : IHostedService, IDisposable
{
    private NetworkStream? stream;
    private readonly ILogger<BpqApplicationService> logger;
    private readonly MqttPublisher mqttPublisher;

    public BpqApplicationService(ILogger<BpqApplicationService> logger, MqttPublisher mqttPublisher)
    {
        this.logger = logger;
        this.mqttPublisher = mqttPublisher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(Run, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task Run()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 63001);
        tcpListener.Start();

        while (true)
        {
            try
            {
                logger.LogInformation("Listening...");
                using var client = await tcpListener.AcceptTcpClientAsync();
                logger.LogInformation("Accepted client");

                stream = client.GetStream();
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                using var reader = new StreamReader(stream);

                var callsign = await reader.ReadLineAsync();
                logger.LogInformation($"Accepted connection from {callsign}");

                writer.Write("This is DAPPS\r");

                using var binaryReader = new BinaryReader(stream);

                while (true)
                {
                    while (!stream.DataAvailable)
                    {
                        await Task.Delay(100);
                    }

                    var messageLength = binaryReader.ReadInt16();

                    logger.LogInformation("Message length: {length}", messageLength);

                    byte[] data = new byte[messageLength];
                    for (int i = 0; i < messageLength; i++)
                    {
                        data[i] = (byte)stream.ReadByte();
                    }

                    var s = Encoding.UTF8.GetString(data);

                    if (s.Trim() == "/bye")
                    {
                        logger.LogInformation("Client said bye");
                        break;
                    }

                    logger.LogInformation("Received '{data}'", s);

                    var request = Request.FromOnAirFormat(data);

                    await mqttPublisher.Publish($"dapps/apps/{request.AppName}/in/{request.SourceCall}", request.Payload);

                    logger.LogInformation("Sending back OK");

                    writer.Write("OK\r");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Caught exception");
                await Task.Delay(1000);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stream?.Close();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        stream?.Dispose();
    }
}
