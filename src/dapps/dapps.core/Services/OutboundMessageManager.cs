using dapps.client;
using dapps.client.Transport;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

public class OutboundMessageManager(
    Database database,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<SystemOptions> options,
    IDappsOutboundTransport transport)
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

            var bpqPort = neighbour.BpqPort ?? optionsValue.DefaultBpqPort;

            try
            {
                await using var connection = await transport.ConnectAsync(
                    localCallsign: optionsValue.Callsign,
                    remoteCallsign: neighbour.Callsign,
                    bpqPortNumber: bpqPort,
                    stoppingToken: stoppingToken);

                var protocol = new DappsProtocolClient(connection.Stream, loggerFactory);

                if (!await protocol.ReadInitialPromptAsync(stoppingToken))
                {
                    logger.LogError("Did not see DAPPSv1> prompt from {0}, skipping {1}", neighbour.Callsign, message.Id);
                    continue;
                }

                if (!await protocol.OfferMessageAsync(
                        message.Id, message.Salt, DappsMessage.MessageFormat.Plain,
                        message.Destination, message.Payload.Length, stoppingToken, ttl: residualTtl))
                {
                    logger.LogError("Message offer was not accepted for {0}", message.Id);
                    continue;
                }

                if (!await protocol.SendMessageAsync(message.Id, message.Payload, stoppingToken))
                {
                    logger.LogError("Message payload was not accepted for {0}", message.Id);
                    continue;
                }

                logger.LogInformation("Remote end accepted message {0}", message.Id);
                await database.MarkMessageAsForwarded(message.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to forward message {0} to {1}", message.Id, neighbour.Callsign);
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
