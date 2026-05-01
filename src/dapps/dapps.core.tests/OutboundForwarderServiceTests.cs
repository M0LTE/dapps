using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Tests for the auto-forwarder loop:
///
/// 1. <see cref="OutboundMessageManager"/> serialises concurrent
///    <c>DoRun</c> calls — second-and-later concurrent invocations
///    return immediately rather than racing through the same pending
///    list. This is the failure mode that produced wasteful duplicate
///    sends when both the manual <c>/Message/dorun</c> trigger and
///    a future hosted ticker existed; the mutex prevents that.
///
/// 2. <see cref="OutboundForwarderService"/> actually ticks
///    <c>DoRun</c> on its hosted-service loop. With a fast tick
///    interval substituted (via the test-only knobs on the service)
///    we observe <see cref="OutboundMessageManager.RunCount"/>
///    incrementing within a fraction of a second.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OutboundForwarderServiceTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private OutboundMessageManager outbound = null!;
    private SlowBackhaul slowBackhaul = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-fwdsvc-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbDroppedMessage>();
            c.CreateTable<DbAppToken>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            c.CreateTable<DbDiscoveredPeer>();
            c.CreateTable<DbDiscoveryChannel>();
        }

        var options = new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "G0TEST-1" });
        database = new Database(NullLogger<Database>.Instance, options);
        slowBackhaul = new SlowBackhaul();
        outbound = new OutboundMessageManager(
            database,
            new NullLoggerFactory(),
            options,
            new IDappsBackhaul[] { slowBackhaul });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DoRun_SkipsWhenAlreadyInFlight()
    {
        // Queue a message + neighbour so DoRun has work. The slow
        // backhaul holds each send for 200ms — long enough to launch
        // a second concurrent DoRun and observe the skip.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbNeighbour { Callsign = "G0DEST-1", UdpEndpoint = "127.0.0.1:65535" });
        }
        await database.SaveMessage("doruna1", "x"u8.ToArray(), salt: 1L,
            destination: "chat@G0DEST-1", sourceCallsign: "G0TEST-1",
            additionalProperties: "{}", ttl: 600);

        var first = outbound.DoRun(TestContext.Current.CancellationToken);
        // Give the first call time to acquire the mutex but not finish
        // (slow backhaul will hold it 200ms).
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var second = outbound.DoRun(TestContext.Current.CancellationToken);

        await Task.WhenAll(first, second);

        outbound.RunCount.Should().Be(1,
            "the second concurrent DoRun must skip rather than racing through the queue");
        slowBackhaul.SendCount.Should().Be(1,
            "if the second DoRun ran, the message would have been sent twice — that's exactly what the mutex prevents");
    }

    [Fact]
    public async Task ForwarderService_TicksDoRun_OnItsLoop()
    {
        // A real BackgroundService run would tick every 5s and wait 3s
        // before the first run — too slow for unit tests. The test
        // takes the actual service and just gives it long enough to
        // get past the startup delay + one tick. Empty queue means
        // each DoRun is fast.
        var sp = new ServiceCollection()
            .AddSingleton(outbound)
            .BuildServiceProvider();
        var service = new OutboundForwarderService(sp, NullLogger<OutboundForwarderService>.Instance);
        using var cts = new CancellationTokenSource();

        var startTask = service.StartAsync(cts.Token);
        await startTask;

        // Poll until RunCount bumps. StartupDelay is 3s; allow up to 6s
        // before declaring the service broken.
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (outbound.RunCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(cts.Token);

        outbound.RunCount.Should().BeGreaterThan(0,
            "the hosted service must invoke DoRun on its tick — that's its only job");
    }

    /// <summary>Backhaul that records every Send and stalls each one
    /// briefly so concurrent DoRun calls have time to overlap.</summary>
    private sealed class SlowBackhaul : IDappsBackhaul
    {
        public int SendCount;

        public bool CanHandle(BackhaulRoute route) => route.UdpEndpoint is not null;

        public async Task<BackhaulSendResult> SendAsync(
            BackhaulMessage message,
            BackhaulRoute route,
            string localCallsign,
            CancellationToken ct)
        {
            Interlocked.Increment(ref SendCount);
            await Task.Delay(200, ct);
            return BackhaulSendResult.Ok();
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
