using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace dapps.Services
{
    public class MqttPublisher
    {
        private readonly ILogger<MqttPublisher> logger;
        private readonly IManagedMqttClient mqttClient;

        private const string mqttHost = "localhost";

        public MqttPublisher(ILogger<MqttPublisher> logger)
        {
            this.logger = logger;
            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithClientId(Guid.NewGuid().ToString())
                    .WithTcpServer(mqttHost)
                    .Build())
                .Build();

            mqttClient = new MqttFactory().CreateManagedMqttClient();
            mqttClient.StartAsync(options).GetAwaiter().GetResult();
        }

        public async Task Publish(string topic, byte[] payload)
        {
            logger.LogInformation("Requested to publish {bytes} bytes to topic {topic}", payload.Length, topic);

            try
            {
                await mqttClient.EnqueueAsync(new MqttApplicationMessage { PayloadSegment = payload, Topic = topic });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "While publishing");
                return;
            }

            logger.LogInformation("Published {bytes} bytes to topic {topic}", payload.Length, topic);
        }
    }
}