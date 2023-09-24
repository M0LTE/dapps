using System.Net;
using System.Net.Sockets;

namespace dapps.Services;

/// <summary>
/// Service that sits on a TCP port and accepts application connections from BPQ, handing work off to another task.
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
        _ = Task.Run(Run, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task Run()
    {
        var tcpListener = new TcpListener(IPAddress.Any, 63001);
        tcpListener.Start();

        while (true)
        {
            try
            {
                logger.LogInformation("Listening for connection from BPQ...");
                var client = await tcpListener.AcceptTcpClientAsync();
                logger.LogInformation("Accepted client");
                _ = Task.Run(() => inboundConnectionHandlerService.Handle(client.GetStream()));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Caught exception");
                await Task.Delay(1000);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
