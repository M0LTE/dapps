using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// Smoke test for the two-instance AXIP-UDP-tunneled AGW topology
/// (issue #6). Asserts that an AGW connect from one BPQ's APPL call to
/// the other BPQ's APPL call dispatches into a registered AGW listener
/// on the receiving side. Without the entrypoint-bypass fix, the
/// inbound 'C' frame never fires — see <see cref="TwoInstanceLinbpqFixture"/>.
///
/// This is the gate for an end-to-end DAPPS forwarding integration test
/// (TTL decrement across hops, etc.); having proven the BPQ side works
/// here lets DAPPS-side tests build on this fixture.
/// </summary>
[Collection("Linbpq two-instance integration")]
[Trait("Category", "Integration")]
public class TwoInstanceAgwSmokeTests(TwoInstanceLinbpqFixture fixture)
{
    [Fact]
    public async Task ConnectFromAtoB_DispatchesToRegisteredListener()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = ctSource.Token;

        using var tcpA = new TcpClient();
        await tcpA.ConnectAsync(fixture.Host, fixture.AgwPortA, ct);
        var sideA = new AgwFrameTransport(tcpA.GetStream());

        using var tcpB = new TcpClient();
        await tcpB.ConnectAsync(fixture.Host, fixture.AgwPortB, ct);
        var sideB = new AgwFrameTransport(tcpB.GetStream());

        // Register the APPL callsigns on both sides as AGW listeners.
        await sideA.WriteFrameAsync(new AgwFrame(0, 'X', 0, fixture.ApplCallA, "", []), ct);
        await sideB.WriteFrameAsync(new AgwFrame(0, 'X', 0, fixture.ApplCallB, "", []), ct);

        // Drain the 'X' acks so they don't sit ahead of the 'C' confirms.
        // BPQ replies to 'X' with a single byte payload (0x00 = success).
        var ackA = await sideA.ReadFrameAsync(ct);
        var ackB = await sideB.ReadFrameAsync(ct);
        ackA.Kind.Should().Be('X');
        ackB.Kind.Should().Be('X');

        // Connect A's APPL → B's APPL, routed via the AXIP carrier port.
        await sideA.WriteFrameAsync(
            new AgwFrame((byte)fixture.AxipPortIndex, 'C', 0, fixture.ApplCallA, fixture.ApplCallB, []),
            ct);

        // Both sides should see a 'C' confirm. A's CONNECTED arrives within a
        // second or two; B's takes a moment longer.
        var confirmA = await ReadUntil(sideA, 'C', ct);
        var confirmB = await ReadUntil(sideB, 'C', ct);

        Encoding.ASCII.GetString(confirmA.Payload).Should().Contain("CONNECTED");
        Encoding.ASCII.GetString(confirmB.Payload).Should().Contain("CONNECTED");

        // Tear the AX.25 link down cleanly so subsequent tests in this
        // collection don't get a stale-link surprise when they reuse the
        // same APPL call pair.
        await sideA.WriteFrameAsync(
            new AgwFrame((byte)fixture.AxipPortIndex, 'd', 0, fixture.ApplCallA, fixture.ApplCallB, []),
            ct);
        // Wait a beat for BPQ to propagate the DISC and tear down link state
        // on both sides before we close the AGW sockets.
        await Task.Delay(2000, ct);
    }

    private static async Task<AgwFrame> ReadUntil(AgwFrameTransport transport, char kind, CancellationToken ct)
    {
        while (true)
        {
            var frame = await transport.ReadFrameAsync(ct);
            if (frame.Kind == kind) return frame;
            // ignore other kinds (status updates, etc.) and keep reading
        }
    }
}
