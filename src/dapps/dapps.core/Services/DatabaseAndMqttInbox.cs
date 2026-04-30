using System.Text.Json;
using dapps.client.Backhaul;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Default <see cref="IBackhaulInbox"/> for this node: persists the
/// received message into the SQLite queue and, if the destination is a
/// local app, pushes it to the MQTT broker for any connected subscriber.
///
/// Bearer-neutral — this code doesn't know whether the message arrived
/// over a DAPPSv1 session or a MeshCore datagram. The bearer-specific
/// receive layer is the one that called <see cref="DeliverAsync"/>.
/// </summary>
public sealed class DatabaseAndMqttInbox(
    Database database,
    MqttBrokerService mqtt,
    InboundEventBus events,
    IOptionsMonitor<SystemOptions> options,
    ILogger<DatabaseAndMqttInbox> logger) : IBackhaulInbox
{
    public async Task DeliverAsync(
        BackhaulMessage message,
        string sourceCallsign,
        CancellationToken ct)
    {
        var headersJson = message.Headers is null
            ? "{}"
            : JsonSerializer.Serialize(message.Headers);

        await database.SaveMessage(
            message.Id,
            message.Payload,
            message.Salt,
            message.Destination,
            sourceCallsign,
            headersJson,
            message.Ttl);

        if (DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign))
        {
            var dbMessage = new DbMessage
            {
                Id = message.Id,
                Payload = message.Payload,
                Salt = message.Salt,
                Destination = message.Destination,
                SourceCallsign = sourceCallsign,
                AdditionalProperties = headersJson,
                Ttl = message.Ttl,
            };
            await mqtt.InjectInboundMessage(dbMessage);
        }
        else
        {
            logger.LogDebug("Message {0} for {1} is not local — leaving in queue for forwarding",
                message.Id, message.Destination);
        }

        // Notify dashboard SSE subscribers regardless of local-vs-relay —
        // operators want to see traffic flowing through the node.
        events.Publish(new InboundEvent(
            ReceivedAt: DateTime.UtcNow,
            Id: message.Id,
            SourceCallsign: sourceCallsign,
            Destination: message.Destination,
            PayloadLength: message.Payload.Length,
            Ttl: message.Ttl));
    }
}
