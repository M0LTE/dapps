using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;

namespace dapps.core.tests;

/// <summary>
/// Wire-level smoke for <see cref="Rhpv2InboundService"/> against
/// rhp2lib-net's <c>MockRhpServer</c>. Locks the startup-frame sequence
/// the listener produces (SOCKET → BIND → LISTEN), with the local
/// callsign passed through verbatim. End-to-end delivery (ACCEPT →
/// per-child stream → DAPPSv1 parsing) is covered by the
/// scripts/sim-mixed-bearer.sh end-to-end run, not here.
/// </summary>
public sealed class Rhpv2InboundServiceTests
{
    [Fact]
    public async Task Startup_BindsLocalCallsign_AndListensPassive()
    {
        await using var server = new MockRhpServer();
        server.Start();

        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G0DPB-1",
            NodeHost = server.Endpoint.Address.ToString(),
            RhpPort = server.Endpoint.Port,
            // No auth - leave RhpUser/RhpPass empty.
        });

        var database = new Database(NullLogger<Database>.Instance, opts);
        var inbox = new NoopInbox();
        var metrics = new OperationalMetrics();
        var service = new Rhpv2InboundService(
            opts, NullLoggerFactory.Instance, NullLogger<Rhpv2InboundService>.Instance,
            database, inbox, metrics);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            var frames = WaitForFrames(server, count: 3, TimeSpan.FromSeconds(2));
            frames[0].Should().BeOfType<SocketMessage>();
            frames[1].Should().BeOfType<BindMessage>();
            frames[2].Should().BeOfType<ListenMessage>();

            var socket = (SocketMessage)frames[0];
            socket.Pfam.Should().Be(ProtocolFamily.Ax25);
            socket.Mode.Should().Be(SocketMode.Stream);

            var bind = (BindMessage)frames[1];
            bind.Local.Should().Be("G0DPB-1",
                "the inbound listener binds the daemon's own callsign so dispatch matches L2 SABM addressed to it");
            bind.Port.Should().BeNull(
                "port=null asks XRouter to listen across all configured ports rather than constraining to one");

            var listen = (ListenMessage)frames[2];
            ((OpenFlags)listen.Flags & OpenFlags.Active).Should().Be(0,
                "inbound is passive - active would issue a connect");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_WithRhpAuth_AuthenticatesBeforeSocket()
    {
        await using var server = new MockRhpServer
        {
            RequireAuth = true,
            Credentials = ("op", "pw"),
        };
        server.Start();

        var opts = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "G0DPB-1",
            NodeHost = server.Endpoint.Address.ToString(),
            RhpPort = server.Endpoint.Port,
            RhpUser = "op",
            RhpPass = "pw",
        });

        var database = new Database(NullLogger<Database>.Instance, opts);
        var inbox = new NoopInbox();
        var metrics = new OperationalMetrics();
        var service = new Rhpv2InboundService(
            opts, NullLoggerFactory.Instance, NullLogger<Rhpv2InboundService>.Instance,
            database, inbox, metrics);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        try
        {
            var frames = WaitForFrames(server, count: 4, TimeSpan.FromSeconds(2));
            frames[0].Should().BeOfType<AuthMessage>(
                "AUTH must precede SOCKET when RhpUser is configured");
            frames[1].Should().BeOfType<SocketMessage>();
            frames[2].Should().BeOfType<BindMessage>();
            frames[3].Should().BeOfType<ListenMessage>();

            var auth = (AuthMessage)frames[0];
            auth.User.Should().Be("op");
            auth.Pass.Should().Be("pw");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class NoopInbox : IBackhaulInbox
    {
        public Task DeliverAsync(BackhaulMessage message, string sourceCallsign, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static List<RhpMessage> WaitForFrames(MockRhpServer server, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.ReceivedFrames.Count >= count) return server.ReceivedFrames.Take(count).ToList();
            Thread.Sleep(10);
        }
        throw new TimeoutException(
            $"Did not observe {count} frames within {timeout}; saw [{string.Join(", ", server.ReceivedFrames.Select(f => f.GetType().Name))}]");
    }
}
