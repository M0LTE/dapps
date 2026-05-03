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
    private readonly IBackhaulInbox? opportunisticInbox;
    private readonly Func<bool>? opportunisticEnabled;

    public Dappsv1SessionBackhaul(IDappsOutboundTransport transport, ILoggerFactory loggerFactory)
        : this(transport, loggerFactory, opportunisticInbox: null, opportunisticEnabled: null)
    {
    }

    public Dappsv1SessionBackhaul(
        IDappsOutboundTransport transport,
        ILoggerFactory loggerFactory,
        IBackhaulInbox? opportunisticInbox,
        Func<bool>? opportunisticEnabled)
    {
        this.transport = transport;
        this.loggerFactory = loggerFactory;
        this.opportunisticInbox = opportunisticInbox;
        this.opportunisticEnabled = opportunisticEnabled;
        logger = loggerFactory.CreateLogger<Dappsv1SessionBackhaul>();
    }

    /// <summary>
    /// AGW handles any route that does not specify a higher-priority
    /// bearer like UDP. Effectively: this is the fallback bearer when
    /// only callsign + BPQ port are known.
    /// </summary>
    public bool CanHandle(BackhaulRoute route) => route.UdpEndpoint is null;

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
                    ttl: message.Ttl,
                    originator: message.Originator,
                    masterId: message.MasterId,
                    fragmentIndex: message.FragmentIndex,
                    fragmentTotal: message.FragmentTotal))
            {
                return BackhaulSendResult.Fail($"offer rejected for {message.Id}");
            }

            if (!await protocol.SendMessageAsync(message.Id, message.Payload, ct))
            {
                return BackhaulSendResult.Fail($"payload rejected for {message.Id}");
            }

            // Plan F3 - opportunistic poll. The session is open, the
            // ack just landed; if the operator's enabled the feature
            // and we have a place to deliver inbound, send `rev` and
            // drain anything the remote has queued for us. Failures
            // here don't flip the SendResult to fail - the push was
            // the actual ask, the drain is a bonus.
            if (opportunisticInbox is not null && (opportunisticEnabled?.Invoke() ?? false))
            {
                try
                {
                    await foreach (var polled in protocol.PollAsync(requestedIds: null, ct))
                    {
                        var inbound = new BackhaulMessage(
                            Id: polled.Id,
                            Destination: polled.Destination,
                            Salt: polled.Salt,
                            Ttl: polled.Ttl,
                            Payload: polled.Payload,
                            Originator: polled.Originator,
                            MasterId: polled.MasterId,
                            FragmentIndex: polled.FragmentIndex,
                            FragmentTotal: polled.FragmentTotal);
                        await opportunisticInbox.DeliverAsync(inbound, route.Callsign, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Opportunistic poll of {0} failed (push already succeeded)", route.Callsign);
                }
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
