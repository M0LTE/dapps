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
/// Bearer-neutral - this code doesn't know whether the message arrived
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
    TimeProvider timeProvider,
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
        // key is (id, link-source) - different upstreams might be legitimate
        // independent floods of distinct messages with the same id (rare,
        // but possible if two senders salt-collide). Including the link
        // source means we'll dedup re-arrivals from the SAME upstream
        // without conflating them with arrivals from DIFFERENT upstreams.
        if (message.FloodHopsRemaining is { } _)
        {
            if (await routingContext.HasSeenFloodAsync(message.Id, sourceCallsign, ct))
            {
                logger.LogDebug("Flood {0} from {1} dropped - already seen", message.Id, sourceCallsign);
                return;
            }
            await routingContext.RecordFloodSeenAsync(message.Id, sourceCallsign, ct);
        }

        // Hand the message to the routing algorithm BEFORE persistence -
        // passive-learning algorithms care about the (originator, link-source)
        // pair, and that pair is only meaningful here at the wire boundary.
        // Algorithms that don't observe inbound (StaticRoutingAlgorithm) no-op
        // immediately.
        await routingAlgorithm.ObserveInboundAsync(message, sourceCallsign, routingContext, ct);

        var headersJson = message.Headers is null
            ? "{}"
            : JsonSerializer.Serialize(message.Headers);

        var originator = message.Originator ?? "";

        // F2 multi-part - destination-side reassembly. Fragments
        // destined for a local app go into the reassembly buffer
        // instead of the regular DbMessage table; the regular table
        // gets the assembled payload as one row when the last fragment
        // arrives. Intermediate hops (where IsLocal is false) just
        // forward each fragment as an opaque message - they take the
        // normal SaveMessage path below.
        var isFragmentForLocal = message.MasterId is not null
            && message.FragmentIndex.HasValue
            && message.FragmentTotal.HasValue
            && DestinationParser.IsLocal(message.Destination, options.CurrentValue.Callsign);
        if (isFragmentForLocal)
        {
            await HandleLocalFragmentAsync(message, sourceCallsign, originator, headersJson, ct);
            // Notify SSE so operators see fragments arriving in real time.
            // Fragment rows aren't in DbMessage so the normal Recent-
            // messages panel won't show them; the event stream is the
            // only signal until reassembly completes.
            events.Publish(new InboundEvent(
                ReceivedAt: timeProvider.GetUtcNow().UtcDateTime,
                Id: message.Id,
                SourceCallsign: sourceCallsign,
                Destination: message.Destination,
                PayloadLength: message.Payload.Length,
                Ttl: message.Ttl));
            return;
        }

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
            // next forwarder tick - e.g. re-flood with the right
            // accumulator after a process restart.
            sourceRouteCsv: message.SourceRoute is { Count: > 0 }
                ? string.Join(',', message.SourceRoute)
                : (message.SourceRoute is null ? null : ""),
            traversedHopsCsv: message.TraversedHops is null
                ? null
                : string.Join(',', message.TraversedHops),
            // F2 multi-part: preserve the master id + fragment index/total
            // on transit rows so the forwarder re-emits them on the next
            // hop. (Local-fragment-for-this-node took the early-return
            // path above; this branch only fires for non-fragmented
            // messages OR fragments-for-elsewhere.)
            masterId: message.MasterId,
            fragmentIndex: message.FragmentIndex,
            fragmentTotal: message.FragmentTotal);

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
            logger.LogDebug("Message {0} for {1} is not local - leaving in queue for forwarding",
                message.Id, message.Destination);
        }

        // Notify dashboard SSE subscribers regardless of local-vs-relay -
        // operators want to see traffic flowing through the node.
        events.Publish(new InboundEvent(
            ReceivedAt: timeProvider.GetUtcNow().UtcDateTime,
            Id: message.Id,
            SourceCallsign: sourceCallsign,
            Destination: message.Destination,
            PayloadLength: message.Payload.Length,
            Ttl: message.Ttl));
    }

    /// <summary>
    /// Plan F2 - destination-side fragment handling. Stores the
    /// fragment in <see cref="DbFragment"/>, checks whether the full
    /// set is now present, and if so reassembles + delivers the
    /// assembled payload via the regular DbMessage + MQTT path.
    /// Pre: <paramref name="message"/> has MasterId / FragmentIndex /
    /// FragmentTotal all set, AND the destination is local to this
    /// node.
    /// </summary>
    private async Task HandleLocalFragmentAsync(
        BackhaulMessage message, string sourceCallsign, string originator, string headersJson, CancellationToken ct)
    {
        var masterId = message.MasterId!;
        var fragIndex = message.FragmentIndex!.Value;
        var fragTotal = message.FragmentTotal!.Value;

        await database.UpsertFragment(new DbFragment
        {
            MasterId = masterId,
            FragmentIndex = fragIndex,
            FragmentTotal = fragTotal,
            Payload = message.Payload,
            Destination = message.Destination,
            SourceCallsign = sourceCallsign,
            OriginatorCallsign = originator,
            AdditionalProperties = headersJson,
            Ttl = message.Ttl,
            FirstSeenAt = timeProvider.GetUtcNow().UtcDateTime,
        });

        var fragments = await database.GetFragmentsForMaster(masterId);
        // Distinct-by-index covers the (rare) case where a fragment
        // is re-delivered via a different path before its sibling
        // arrives - the upsert above keeps a single row per index, so
        // Count here = unique-index count.
        if (fragments.Count < fragTotal)
        {
            logger.LogInformation(
                "Fragment {Index} of {Total} for master {MasterId} stored ({Have} so far) - waiting for siblings",
                fragIndex, fragTotal, masterId, fragments.Count);
            return;
        }

        // All fragments present - assemble. Concatenate by 1-based
        // FragmentIndex. The fragments table query already orders.
        var totalLen = fragments.Sum(f => f.Payload.Length);
        var assembled = new byte[totalLen];
        var offset = 0;
        foreach (var f in fragments)
        {
            Buffer.BlockCopy(f.Payload, 0, assembled, offset, f.Payload.Length);
            offset += f.Payload.Length;
        }

        // Use the master id as the assembled message's id - it's
        // the natural content-addressable identifier from the
        // originator's view, and the dashboard / app-interface
        // dapps-id user property reads coherently from sender to
        // receiver. Salt is the FIRST fragment's salt for traceability.
        var firstSeen = fragments.Min(f => f.FirstSeenAt);
        var assembledTtl = message.Ttl;
        await database.SaveMessage(
            id: masterId,
            buffer: assembled,
            salt: null,
            destination: message.Destination,
            sourceCallsign: sourceCallsign,
            additionalProperties: headersJson,
            ttl: assembledTtl,
            originatorCallsign: originator);

        // Inject the assembled message into MQTT exactly as a
        // single-part arrival would have been.
        var assembledRow = new DbMessage
        {
            Id = masterId,
            Payload = assembled,
            Salt = null,
            Destination = message.Destination,
            SourceCallsign = sourceCallsign,
            OriginatorCallsign = originator,
            AdditionalProperties = headersJson,
            Ttl = assembledTtl,
            CreatedAt = firstSeen,
        };
        await mqtt.InjectInboundMessage(assembledRow);

        // Drop fragment rows now that the assembled message is in DbMessage.
        await database.DeleteFragmentsForMaster(masterId);

        logger.LogInformation(
            "Reassembled master {0} ({1} fragments, {2} bytes) - delivered as one message",
            masterId, fragTotal, totalLen);
    }
}
