using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Regression test for the operator UX of the MQTT-port-conflict crash
/// path. Earlier (v0.15.0 deploy on gb7rdg-node) DAPPS hit this in the
/// wild - a co-located mosquitto container held :1883, the embedded
/// broker raised SocketException, and the host crash-looped via
/// systemd's Restart=on-failure printing a noisy stack trace each
/// time. Fixed by catching SocketException(AddressAlreadyInUse) at
/// startup and rethrowing as InvalidOperationException with a single
/// actionable message that points the operator at /Config or a
/// different DAPPS_MQTT_PORT.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class MqttBrokerBindErrorTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(),
            $"dapps-mqtt-bind-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbSystemOption>();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_PortAlreadyHeldBySomeoneElse_ThrowsActionableMessage()
    {
        // Hold a TCP port with a vanilla listener - same shape as a
        // co-located docker mosquitto on the host's :1883.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var heldPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
            {
                Callsign = "N0CALL",
                MqttPort = heldPort,
            });
            var database = new Database(NullLogger<Database>.Instance, optionsMonitor);
            var tokens = new AppTokenStore(NullLogger<AppTokenStore>.Instance);
            var broker = new MqttBrokerService(
                NullLogger<MqttBrokerService>.Instance, optionsMonitor, database, tokens);

            Func<Task> act = () => broker.StartAsync(CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
            ex.Message.Should().Contain($"port {heldPort}");
            ex.Message.Should().Contain("already in use");
            // Operator-facing breadcrumb: tell them where to change it.
            ex.Message.Should().Contain("/Config");
            // Inner cause preserved so a developer who needs the
            // bind-level failure can still drill into it.
            ex.InnerException.Should().BeOfType<SocketException>();
            ((SocketException)ex.InnerException!).SocketErrorCode
                .Should().Be(SocketError.AddressAlreadyInUse);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
