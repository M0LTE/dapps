using dapps.client;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

public class OutboundMessageManager(Database database, ILoggerFactory loggerFactory, IOptionsMonitor<SystemOptions> options)
{
    public async Task DoRun()
    {
        var logger = loggerFactory.CreateLogger<OutboundMessageManager>();

        logger.LogInformation("Starting a run");

        var optionsValue = options.CurrentValue;

        var messages = await database.GetPendingOutboundMessages();
        var neighhours = await database.GetNeighbours();

        foreach (var message in messages)
        {
            DbRouteHint? routeHint;
            DbNeighbour? neighbour = neighhours.FirstOrDefault(n => n.Callsign.Split('-')[0].Equals(message.Destination, StringComparison.OrdinalIgnoreCase));

            if (neighbour == null)
            {
                var destSystem = message.Destination.Split('@').Last();

                routeHint = await database.GetRouteHint(destSystem);

                if (routeHint == null)
                {
                    routeHint = await database.GetRouteHint("*");

                    if (routeHint == null)
                    {
                        logger.LogWarning("No route hint and no default route set, skipping message");
                        continue;
                    }
                    else
                    {
                        logger.LogWarning("No specific route hint for {0}, passing to default partner", message.Destination);
                    }
                }

                logger.LogInformation("Routing message {0} for {1} to {2}", message.Id, message.Destination, routeHint.NextHop);
                neighbour = await database.GetNeighbour(routeHint.NextHop);
            }

            if (neighbour == null)
            {
                logger.LogWarning("No neighbour for {0}, skipping message", message.Id);
                continue;
            }

            var dappsClient = new DappsFbbClient(optionsValue.NodeHost, optionsValue.FbbPort, loggerFactory);

            if (!await dappsClient.FbbLogin(optionsValue.FbbUser, optionsValue.FbbPassword))
            {
                logger.LogInformation("FBB login failed, skipping message {0}", message.Id);
                return;
            }

            if (!await dappsClient.ConnectToDappsInstance(neighbour.ConnectScript.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                logger.LogError("Could not connect to neighbour DAPPS instance {0}", neighbour.Callsign);
                return;
            }

            if (!await dappsClient.OfferMessage(message.Id, message.Timestamp, DappsMessage.MessageFormat.Plain, message.Destination, message.Payload.Length))
            {
                logger.LogError("Message offer was not accepted for {id}", message.Id);
                return;
            }

            if (!await dappsClient.SendMessage(message.Id, message.Payload))
            {
                logger.LogError("Message payload was not sent for {id}", message.Id);
                return;
            }

            logger.LogInformation("Remote end accepted message {0}", message.Id);
            await database.MarkMessageAsForwarded(message.Id);
        }
    }
}
