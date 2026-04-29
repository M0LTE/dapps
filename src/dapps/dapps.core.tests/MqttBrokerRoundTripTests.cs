using System.Net;
using System.Net.Sockets;
using System.Text;
using dapps.core.Models;
using dapps.core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// End-to-end MQTT roundtrips against the real embedded broker, using a
/// real MQTTnet client. Each test gets its own SQLite file + broker on a
/// free port.
/// </summary>
public sealed class MqttBrokerRoundTripTests : IAsyncLifetime
{
    private string dbPath = null!;
    private int brokerPort;
    private Database database = null!;
    private MqttBrokerService broker = null!;

    public async Task InitializeAsync()
    {
        brokerPort = PickFreeTcpPort();
        dbPath = Path.Combine(Path.GetTempPath(),
            $"dapps-mqtt-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        // Recreate schema in the temp DB.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbSystemOption>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbNeighbour>();
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            MqttPort = brokerPort,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        broker = new MqttBrokerService(
            NullLogger<MqttBrokerService>.Instance, optionsMonitor, database);

        await broker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await broker.StopAsync(CancellationToken.None);
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ClientPublishToOutTopic_PersistsMessageInDb()
    {
        var client = await ConnectClient();

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/out/myapp/N0DEST-1")
            .WithPayload("Hello world"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        // Settle: broker dispatches publish on a worker thread.
        await Eventually(async () =>
        {
            var pending = await database.GetPendingOutboundMessages();
            return pending.Count == 1;
        });

        var pending = await database.GetPendingOutboundMessages();
        pending.Should().HaveCount(1);
        var msg = pending.Single();
        msg.Destination.Should().Be("myapp@N0DEST-1");
        Encoding.UTF8.GetString(msg.Payload).Should().Be("Hello world");
        msg.Forwarded.Should().BeFalse();

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SubscribeToInTopic_ReplaysUnacknowledgedMessages()
    {
        // Pre-load a local-destined message into the DB, stamped with the
        // callsign of the node that delivered it.
        var payload = Encoding.UTF8.GetBytes("hi from another node");
        await database.SaveMessage("abc1234", payload, salt: 1L,
            destination: "myapp@N0CALL", sourceCallsign: "G7XYZ-3", additionalProperties: "{}");

        var client = await ConnectClient();
        var received = new TaskCompletionSource<MqttApplicationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async e =>
        {
            received.TrySetResult(e.ApplicationMessage);
            await Task.CompletedTask;
        };

        await client.SubscribeAsync("dapps/in/myapp", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Topic.Should().Be("dapps/in/myapp");
        msg.PayloadSegment.ToArray().Should().Equal(payload);
        msg.UserProperties.Single(p => p.Name == "dapps-id").Value.Should().Be("abc1234");
        msg.UserProperties.Single(p => p.Name == "dapps-source").Value.Should().Be("G7XYZ-3");

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task ClientPublishToAckTopic_MarksMessageDelivered()
    {
        await database.SaveMessage("xyz9999", "data"u8.ToArray(), salt: null,
            destination: "myapp@N0CALL", sourceCallsign: "G7XYZ-3", additionalProperties: "{}");

        var client = await ConnectClient();

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("dapps/ack/myapp")
            .WithPayload("xyz9999"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        await Eventually(async () =>
        {
            var pending = await database.GetUnacknowledgedLocalMessagesForApp("myapp");
            return pending.Count == 0;
        });

        var pending = await database.GetUnacknowledgedLocalMessagesForApp("myapp");
        pending.Should().BeEmpty();

        await client.DisconnectAsync();
    }

    private async Task<IMqttClient> ConnectClient()
    {
        var client = new MqttFactory().CreateMqttClient();
        var opts = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("test-" + Guid.NewGuid().ToString("N")[..6])
            .WithCleanSession(true)
            .Build();
        await client.ConnectAsync(opts);
        return client;
    }

    private static int PickFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
