using dapps.core.Models;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
using System.Text;

namespace dapps.core.Services;

/// <summary>
/// Embedded MQTT broker that fronts the DAPPS app interface.
///
/// Topic structure (from the spec / gist):
///   - `dapps/in/&lt;app&gt;`              — apps subscribe; DAPPS publishes
///   - `dapps/out/&lt;app&gt;/&lt;dest&gt;` — apps publish; DAPPS queues for forwarding
///   - `dapps/ack/&lt;app&gt;`             — apps publish msg-id to ack receipt
///
/// Durability: DAPPS owns the queue (in SQLite). The broker is just a
/// real-time delivery channel. Messages stay in the database until the app
/// explicitly acks; if no subscriber is connected, the publish is a no-op
/// and the message is replayed when a subscriber later connects.
/// </summary>
public sealed class MqttBrokerService(
    ILogger<MqttBrokerService> logger,
    IOptionsMonitor<SystemOptions> options,
    Database database) : IHostedService
{
    private const string OutTopicPrefix = "dapps/out/";
    private const string InTopicPrefix = "dapps/in/";
    private const string AckTopicPrefix = "dapps/ack/";

    private MqttServer? server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;

        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(opts.MqttPort)
            .Build();

        server = new MqttFactory().CreateMqttServer(serverOptions);

        server.InterceptingPublishAsync += OnInterceptingPublish;
        server.ClientSubscribedTopicAsync += OnClientSubscribedTopic;

        await server.StartAsync();
        logger.LogInformation("MQTT broker listening on :{port}", opts.MqttPort);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (server is not null)
        {
            await server.StopAsync();
            server.Dispose();
            server = null;
        }
    }

    /// <summary>
    /// Publish an inbound (received-from-another-DAPPS) message to its
    /// local app's topic. Called by InboundConnectionHandler on receipt.
    /// If no subscriber is connected, the broker drops the publish — the
    /// message is in the DB and will be replayed on next subscribe.
    /// </summary>
    public async Task InjectInboundMessage(DbMessage message)
    {
        if (server is null) return;

        var (app, _) = DestinationParser.Parse(message.Destination);
        if (app.Length == 0) return;

        var topic = InTopicPrefix + app;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(message.Payload)
            .WithUserProperty("dapps-id", message.Id)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg));
            logger.LogDebug("MQTT: injected message {0} onto {1}", message.Id, topic);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT: failed to inject {0} onto {1}", message.Id, topic);
        }
    }

    private async Task OnInterceptingPublish(InterceptingPublishEventArgs e)
    {
        // Internal injections (server-side) have null ClientId — let those through
        // without re-routing. Only intercept publishes from real clients.
        if (string.IsNullOrEmpty(e.ClientId)) return;

        var topic = e.ApplicationMessage.Topic;

        if (topic.StartsWith(OutTopicPrefix, StringComparison.Ordinal))
        {
            // dapps/out/<app>/<dest>
            var rest = topic[OutTopicPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1)
            {
                logger.LogWarning("MQTT: ignoring publish to malformed topic {0}", topic);
                e.ProcessPublish = false;
                return;
            }
            var app = rest[..slash];
            var dest = rest[(slash + 1)..];

            try
            {
                var id = await database.SubmitOutboundMessage(app, dest, e.ApplicationMessage.PayloadSegment.ToArray());
                logger.LogInformation("MQTT: queued outbound {0} from app {1} to {2}", id, app, dest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MQTT: failed to persist outbound publish on {0}", topic);
            }

            // Don't fan out the dapps/out/* publish to subscribers — it's
            // app→DAPPS only.
            e.ProcessPublish = false;
            return;
        }

        if (topic.StartsWith(AckTopicPrefix, StringComparison.Ordinal))
        {
            var id = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
            if (id.Length > 0)
            {
                try
                {
                    await database.MarkLocallyDelivered(id);
                    logger.LogInformation("MQTT: ack received for {0}", id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MQTT: failed to mark {0} delivered", id);
                }
            }

            e.ProcessPublish = false;
            return;
        }

        if (topic.StartsWith(InTopicPrefix, StringComparison.Ordinal))
        {
            // Apps shouldn't be publishing to in/* — it's DAPPS→app only.
            // Block but don't error noisily.
            logger.LogDebug("MQTT: blocking client publish to in-topic {0}", topic);
            e.ProcessPublish = false;
            return;
        }

        // Any other topics are not part of the DAPPS protocol; let them flow
        // (apps may use the broker for their own coordination if they want).
    }

    private async Task OnClientSubscribedTopic(ClientSubscribedTopicEventArgs e)
    {
        var topic = e.TopicFilter.Topic;
        if (!topic.StartsWith(InTopicPrefix, StringComparison.Ordinal)) return;

        var app = topic[InTopicPrefix.Length..];
        if (app.Length == 0 || app.Contains('+') || app.Contains('#')) return; // wildcard, skip replay

        try
        {
            var pending = await database.GetUnacknowledgedLocalMessagesForApp(app);
            logger.LogInformation("MQTT: replaying {0} unacked message(s) to {1} for app {2}",
                pending.Count, e.ClientId, app);

            foreach (var m in pending)
            {
                await InjectInboundMessage(m);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MQTT: replay failed for app {0}", app);
        }
    }
}
