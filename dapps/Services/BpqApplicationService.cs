using System.Net;
using System.Net.Sockets;

namespace dapps.Services;

/// <summary>
/// Service that accepts TCP connections from BPQ's application facility, handing work off to another task.
/// </summary>
internal class BpqApplicationListener : IHostedService
{
    private readonly ILogger<BpqApplicationListener> logger;
    private readonly InboundConnectionHandlerService inboundConnectionHandlerService;

    public BpqApplicationListener(ILogger<BpqApplicationListener> logger, InboundConnectionHandlerService inboundConnectionHandlerService)
    {
        this.logger = logger;
        this.inboundConnectionHandlerService = inboundConnectionHandlerService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(Listen, CancellationToken.None);
        return Task.CompletedTask;
    }

    private readonly CancellationTokenSource cancellationTokenSource = new();

    private async Task Listen()
    {
        var tcpListener = new TcpListener(IPAddress.Any, 63001);
        tcpListener.Start();

        while (true)
        {
            try
            {
                logger.LogInformation("Listening for connection from BPQ...");
                var client = await tcpListener.AcceptTcpClientAsync(cancellationTokenSource.Token);
                logger.LogInformation("Accepted client");
                _ = Task.Run(async () => await AcceptConnection(client.GetStream(), cancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Caught exception");
                await Task.Delay(1000);
            }
        }
    }

    private async Task AcceptConnection(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var streamReader = new StreamReader(stream);
            var callsign = await streamReader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(callsign))
            {
                logger.LogWarning("Failed to read callsign from node connection");
                return;
            }
            logger.LogInformation($"Accepted connection from {callsign}");
            await inboundConnectionHandlerService.Handle(callsign, stream, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Caught exception");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationTokenSource.Cancel();
        return Task.CompletedTask;
    }
}
