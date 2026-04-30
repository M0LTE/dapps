using AwesomeAssertions;
using dapps.client.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// End-to-end multicast loopback. Two bearer instances on the same
/// loopback interface joined to the same multicast group: one
/// announces, the other hears. Validates the bearer end-to-end on
/// real sockets — Linux loopback supports IP multicast natively, so
/// no fixture / docker is needed.
/// </summary>
public sealed class UdpMulticastDiscoveryTests
{
    [Fact]
    public async Task Loopback_AnnounceFromOne_HeardByOther()
    {
        // Distinct ports per test run avoid collisions when xunit runs
        // multiple methods of this class in parallel.
        var port = 41880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";

        await using var sender = new UdpMulticastDiscoveryBearer(group, "M0SEND", NullLoggerFactory.Instance);
        await using var receiver = new UdpMulticastDiscoveryBearer(group, "M0RECV", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.StartAsync(cts.Token);
        await receiver.StartAsync(cts.Token);

        // Background-iterate the receiver's listen stream so we can
        // catch the single beacon we send.
        var heard = new TaskCompletionSource<BeaconFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var b in receiver.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                heard.TrySetResult(b);
                break;
            }
        }, cts.Token);

        // Brief pause so the receiver's read loop is definitely up
        // before we send.
        await Task.Delay(150, cts.Token);

        var beacon = new BeaconFrame("M0SEND", Hops: 0, Ttl: 600, Bearer: new AgwBearerHint(0));
        await sender.AnnounceAsync(beacon, cts.Token);

        var got = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        got.Callsign.Should().Be("M0SEND");
        got.Hops.Should().Be(0);
        got.Ttl.Should().Be(600);
        got.Bearer.Should().BeOfType<UdpBearerHint>();
        ((UdpBearerHint)got.Bearer).Endpoint.Should().Contain(":");
    }

    [Fact]
    public async Task Loopback_OwnBeaconNotEchoedToSelf()
    {
        var port = 42880 + Random.Shared.Next(1000);
        var group = $"239.42.42.42:{port}";

        // Same callsign on send + receive: the receiver should filter
        // out beacons whose callsign matches our own.
        await using var bearer = new UdpMulticastDiscoveryBearer(group, "M0SELF", NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await bearer.StartAsync(cts.Token);

        var sawSomething = new TaskCompletionSource<BeaconFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenTask = Task.Run(async () =>
        {
            await foreach (var b in bearer.ListenAsync(cts.Token).WithCancellation(cts.Token))
            {
                sawSomething.TrySetResult(b);
                break;
            }
        }, cts.Token);

        await Task.Delay(150, cts.Token);
        var beacon = new BeaconFrame("M0SELF", Hops: 0, Ttl: 600, Bearer: new AgwBearerHint(0));
        await bearer.AnnounceAsync(beacon, cts.Token);

        var winner = await Task.WhenAny(
            sawSomething.Task,
            Task.Delay(TimeSpan.FromSeconds(1), cts.Token));
        winner.Should().NotBeSameAs(sawSomething.Task,
            "a node MUST NOT add itself to its own peer table even when its own multicast looped back");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-endpoint")]
    [InlineData("239.42.42.42")]                // missing port
    [InlineData("239.42.42.42:")]               // empty port
    [InlineData("not-an-ip:1881")]              // bad host
    [InlineData("239.42.42.42:99999")]          // port out of range
    public async Task BadGroupSpec_Throws(string group)
    {
        var act = () => new UdpMulticastDiscoveryBearer(group, "M0SELF", NullLoggerFactory.Instance);
        act.Should().Throw<Exception>(); // ArgumentException or FormatException
        await Task.CompletedTask;
    }
}
