using dapps.client.Backhaul;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Pulls pending outbound messages from the queue, computes residual
/// TTL, resolves a neighbour for each, and hands them to the configured
/// <see cref="IDappsBackhaul"/> for delivery. Owns queue/router concerns
/// — it does not know about streams, AGW, or the DAPPSv1 session
/// protocol; those live behind the backhaul seam (Plan A0).
/// </summary>
public class OutboundMessageManager(
    Database database,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<SystemOptions> options,
    IDappsBackhaul backhaul)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<OutboundMessageManager>();

    public async Task DoRun(CancellationToken stoppingToken = default)
    {
        logger.LogInformation("Starting a run");

        var optionsValue = options.CurrentValue;

        var messages = await database.GetPendingOutboundMessages();
        var neighbours = await database.GetNeighbours();

        foreach (var message in messages)
        {
            var residualTtl = TtlMath.Residual(message.Ttl, message.CreatedAt, DateTime.UtcNow);
            if (residualTtl is <= 0)
            {
                logger.LogWarning("Dropping message {0} for {1}: ttl expired ({2}s queued, original ttl={3}s)",
                    message.Id, message.Destination,
                    (int)(DateTime.UtcNow - message.CreatedAt).TotalSeconds, message.Ttl);
                await database.DeleteMessage(message.Id);
                continue;
            }

            var neighbour = await ResolveNeighbour(message, neighbours);
            if (neighbour == null)
            {
                logger.LogWarning("No neighbour for {0}, skipping message", message.Id);
                continue;
            }

            var bm = new BackhaulMessage(
                Id: message.Id,
                Destination: message.Destination,
                Salt: message.Salt,
                Ttl: residualTtl,
                Payload: message.Payload);

            var route = new BackhaulRoute(
                Callsign: neighbour.Callsign,
                BpqPort: neighbour.BpqPort ?? optionsValue.DefaultBpqPort);

            var result = await backhaul.SendAsync(bm, route, optionsValue.Callsign, stoppingToken);
            if (result.Accepted)
            {
                logger.LogInformation("Remote end accepted message {0}", message.Id);
                await database.MarkMessageAsForwarded(message.Id);
            }
            else
            {
                logger.LogError("Failed to forward message {0} to {1}: {2}",
                    message.Id, neighbour.Callsign, result.Error);
            }
        }
    }

    private async Task<DbNeighbour?> ResolveNeighbour(DbMessage message, ICollection<DbNeighbour> neighbours)
    {
        // Destinations are `app@call[-ssid]`. Match a neighbour whose base
        // callsign equals the destination's @-suffix base callsign — an
        // SSID mismatch on either side is fine, the DAPPS instance and
        // its forwarder are reachable on whichever SSID the neighbour row
        // recorded.
        var destBaseCall = message.Destination.Split('@').Last().Split('-')[0];
        var direct = neighbours.FirstOrDefault(
            n => n.Callsign.Split('-')[0].Equals(destBaseCall, StringComparison.OrdinalIgnoreCase));
        if (direct != null)
        {
            return direct;
        }

        // Fall back to a hand-maintained DbRouteHint for next-hop overrides
        // — useful when a destination isn't a direct neighbour but a known
        // neighbour can relay onward. Phase B's auto-discovery will replace
        // this fallback with learned routes.
        var routeHint = await database.GetRouteHint(destBaseCall) ?? await database.GetRouteHint("*");
        if (routeHint == null)
        {
            logger.LogWarning("No matching neighbour or route hint for {0}", message.Destination);
            return null;
        }

        logger.LogInformation("Routing message {0} for {1} via {2}", message.Id, message.Destination, routeHint.NextHop);
        return await database.GetNeighbour(routeHint.NextHop);
    }
}
