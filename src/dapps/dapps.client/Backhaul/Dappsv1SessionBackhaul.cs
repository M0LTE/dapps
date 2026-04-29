using dapps.client.Transport;
using Microsoft.Extensions.Logging;

namespace dapps.client.Backhaul;

/// <summary>
/// Backhaul implementation that uses a stream-shaped bearer (today AGW
/// via <see cref="IDappsOutboundTransport"/>) and speaks the DAPPSv1
/// `prompt` / `ihave` / `send` / `data` / `ack` session protocol.
///
/// Plan A0.2: this is "the BPQ/AGW path" treated as one backend rather
/// than the architectural center. The session protocol logic stays in
/// <see cref="DappsProtocolClient"/>; this class is the thin adapter
/// that translates a single semantic <see cref="BackhaulMessage"/>
/// into the multi-step session exchange.
/// </summary>
public sealed class Dappsv1SessionBackhaul : IDappsBackhaul
{
    private readonly IDappsOutboundTransport transport;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;

    public Dappsv1SessionBackhaul(IDappsOutboundTransport transport, ILoggerFactory loggerFactory)
    {
        this.transport = transport;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<Dappsv1SessionBackhaul>();
    }

    public async Task<BackhaulSendResult> SendAsync(
        BackhaulMessage message,
        BackhaulRoute route,
        string localCallsign,
        CancellationToken ct)
    {
        var bpqPort = route.BpqPort ?? 0;

        try
        {
            await using var connection = await transport.ConnectAsync(
                localCallsign: localCallsign,
                remoteCallsign: route.Callsign,
                bpqPortNumber: bpqPort,
                stoppingToken: ct);

            var protocol = new DappsProtocolClient(connection.Stream, loggerFactory);

            if (!await protocol.ReadInitialPromptAsync(ct))
            {
                return BackhaulSendResult.Fail($"no DAPPSv1> prompt from {route.Callsign}");
            }

            if (!await protocol.OfferMessageAsync(
                    message.Id,
                    message.Salt,
                    DappsMessage.MessageFormat.Plain,
                    message.Destination,
                    message.Payload.Length,
                    ct,
                    ttl: message.Ttl))
            {
                return BackhaulSendResult.Fail($"offer rejected for {message.Id}");
            }

            if (!await protocol.SendMessageAsync(message.Id, message.Payload, ct))
            {
                return BackhaulSendResult.Fail($"payload rejected for {message.Id}");
            }

            return BackhaulSendResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backhaul send failed for {0} to {1}", message.Id, route.Callsign);
            return BackhaulSendResult.Fail(ex.Message);
        }
    }
}
