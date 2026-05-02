using System.Text.Json;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Routing;
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
    IRoutingAlgorithm routingAlgorithm,
    IRoutingContext routingContext,
    ILogger<DatabaseAndMqttInbox> logger) : IBackhaulInbox
{
    public async Task DeliverAsync(
        BackhaulMessage message,
        string sourceCallsign,
        CancellationToken ct)
    {
        // Flood deduplication (B5): a message arriving with FloodHopsRemaining
        // set is part of an in-flight bounded flood. Multiple flood paths can
        // converge at this node from different neighbours; we only process
        // the first arrival and silently drop subsequent copies. The dedup
        // key is (id, link-source) — different upstreams might be legitimate
        // independent floods of distinct messages with the same id (rare,
        // but possible if two senders salt-collide). Including the link
        // source means we'll dedup re-arrivals from the SAME upstream
        // without conflating them with arrivals from DIFFERENT upstreams.
        if (message.FloodHopsRemaining is { } _)
        {
            if (await routingContext.HasSeenFloodAsync(message.Id, sourceCallsign, ct))
            {
                logger.LogDebug("Flood {0} from {1} dropped — already seen", message.Id, sourceCallsign);
                return;
            }
            await routingContext.RecordFloodSeenAsync(message.Id, sourceCallsign, ct);
        }

        // Hand the message to the routing algorithm BEFORE persistence —
        // passive-learning algorithms care about the (originator, link-source)
        // pair, and that pair is only meaningful here at the wire boundary.
        // Algorithms that don't observe inbound (StaticRoutingAlgorithm) no-op
        // immediately.
        await routingAlgorithm.ObserveInboundAsync(message, sourceCallsign, routingContext, ct);

        var headersJson = message.Headers is null
            ? "{}"
            : JsonSerializer.Serialize(message.Headers);

        var originator = message.Originator ?? "";

        await database.SaveMessage(
            message.Id,
            message.Payload,
            message.Salt,
            message.Destination,
            sourceCallsign,
            headersJson,
            message.Ttl,
            originatorCallsign: originator,
            // Floods carry a per-hop budget; preserve it on the queued
            // row so the next forwarder tick re-floods at the right
            // remaining-hops level (decremented by FloodFallbackAlgorithm
            // when it picks this row up). For non-flood messages this
            // stays null.
            floodHopsRemaining: message.FloodHopsRemaining,
            // MeshCore-flavoured routing carries the remaining
            // source route (already stripped by the previous sender)
            // and the accumulated traversal record. Both are
            // persisted so that the algorithm can act on them on the
            // next forwarder tick — e.g. re-flood with the right
            // accumulator after a process restart.
            sourceRouteCsv: message.SourceRoute is { Count: > 0 }
                ? string.Join(',', message.SourceRoute)
                : (message.SourceRoute is null ? null : ""),
            traversedHopsCsv: message.TraversedHops is null
                ? null
                : string.Join(',', message.TraversedHops));

        if (DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign))
        {
            var dbMessage = new DbMessage
            {
                Id = message.Id,
                Payload = message.Payload,
                Salt = message.Salt,
                Destination = message.Destination,
                SourceCallsign = sourceCallsign,
                OriginatorCallsign = originator,
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
