using dapps.core.Models;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace dapps.core.Services;

public class OutboundMessageManager(Database database, ILogger<OutboundMessageManager> logger, BpqFbbPortClient bpqFbbPortClient, IOptions<SystemOptions> options)
{
    public async Task DoRun()
    {
        logger.LogInformation("Starting a run");

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

            if (bpqFbbPortClient.State != BpqFbbPortClient.BpqSessionState.LoggedIn)
            {
                var loginResult = await bpqFbbPortClient.Login(options.Value.BpqFbbUser, options.Value.BpqFbbPassword);

                if (loginResult != FbbLoginResult.Success)
                {
                    logger.LogError("Failed to login to BPQ FBB port");
                    return;
                }
            }

            var stream = bpqFbbPortClient.GetStream();

            var lines = neighbour.ConnectScript.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var line in lines)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r"));
                await stream.FlushAsync();
                await Task.Delay(1000);
            }

            var (gotPrompt, _) = stream!.Expect("DAPPSv1>\n");

            if (!gotPrompt)
            {
                logger.LogError("Could not connect to neighbour {0}", neighbour.Callsign);
                return;
            }

            var dappsMessage = new DappsMessage
            {
                Destination = message.Destination,
                Format = DappsMessage.MessageFormat.Deflate,
                Payload = message.Payload,
                Timestamp = message.Timestamp,
                Kvps = JsonSerializer.Deserialize<Dictionary<string, string>>(message.AdditionalProperties) ?? []
            };

            var ihaveCommand = new IHaveCommand { Message = dappsMessage };

            var cmd = ihaveCommand.ToString();

            await stream.WriteAsync(Encoding.UTF8.GetBytes(cmd + "\n"));
            await stream.FlushAsync();

            var ihaveResponse = stream.ReadUntil(new Dictionary<string, bool> { { $"send {message.Id}", true } });

            if (!ihaveResponse)
            {
                logger.LogError("Remote end did not accept message {0}", message.Id);
                return;
            }

            await stream.WriteAsync(Encoding.UTF8.GetBytes("data " + message.Id + "\n"));
            await stream.WriteAsync(dappsMessage.Payload);
            await stream.FlushAsync();

            var dataResponse = stream.ReadUntil(new Dictionary<string, bool> {
                { $"ack {message.Id}", true },
                { $"nack {message.Id}", false },
            });

            if (dataResponse)
            {
                logger.LogInformation("Remote end accepted message {0}", message.Id);
                await database.MarkMessageAsForwarded(message.Id);
            }
            else
            {
                logger.LogError("Remote end did not accept message {0}", message.Id);
                return;
            }
        }
    }
}