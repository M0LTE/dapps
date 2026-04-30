using System.Net;
using System.Net.Sockets;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Accepts inbound TCP connections from BPQ's APPLICATION+ATTACH bridge.
/// In production BPQ dials this listener whenever a remote station
/// connects to our APPL callsign on a configured port; the first line
/// on the socket is the calling station's callsign (per BPQ's Telnet
/// driver), then bytes flow bidirectionally as a DAPPSv1 session.
/// Port comes from <see cref="SystemOptions.BpqInboundListenerPort"/>.
/// </summary>
public class BpqConnectionListener(
    InboundConnectionHandlerFactory bpqConnectionHandlerFactory,
    IOptionsMonitor<SystemOptions> options,
    ILogger<BpqConnectionListener> logger) : IHostedService
{
    private readonly CancellationTokenSource stoppingTokenSource = new();
    private TcpListener? listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(StartListener, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task StartListener()
    {
        var port = options.CurrentValue.BpqInboundListenerPort;
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        try
        {
            while (!stoppingTokenSource.IsCancellationRequested)
            {
                logger.LogInformation("Waiting for connection on port {0}", listener.LocalEndpoint!.ToString());
                var client = await listener.AcceptTcpClientAsync(stoppingTokenSource.Token);
                var bpqConnectionHandler = bpqConnectionHandlerFactory.Create(client);
                _ = Task.Run(() => bpqConnectionHandler.Handle(stoppingTokenSource.Token));
            }
        }
        catch (OperationCanceledException) when (stoppingTokenSource.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stoppingTokenSource.CancelAsync();
        // Release the OS socket promptly so a follow-up listener (e.g. a
        // sibling test) can rebind without TIME_WAIT contention.
        try { listener?.Stop(); } catch { /* best effort */ }
    }
}
