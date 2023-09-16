using dapps.Models;
using System.Net;
using System.Net.Sockets;
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

                while (true)
                {
                    byte[] buffer = new byte[1024];

                    int bytesRead;

                    try
                    {
                        bytesRead = stream.ReadAtLeast(buffer, 1);
                    }
                    catch (IOException)
                    {
                        logger.LogInformation("Client disconnected");
                        break;
                    }

                    if (bytesRead == -1)
                    {
                        logger.LogInformation("Client disconnected");
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        logger.LogInformation("No bytes read");
                        continue;
                    }

                    var data = buffer[0..bytesRead];

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
