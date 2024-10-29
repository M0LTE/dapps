using System.Net;
using System.Net.Sockets;

namespace dapps.core.Services;

public class BpqConnectionListener(InboundConnectionHandlerFactory bpqConnectionHandlerFactory, ILogger<BpqConnectionListener> logger) : IHostedService
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
