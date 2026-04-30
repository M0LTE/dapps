using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// Pins the BPQ APPLICATION+ATTACH-to-TCP bridge — the inbound delivery
/// path dapps actually uses in production but, prior to issue #32, no
/// test in this repo exercised end-to-end. The AGW dispatch path
/// (TwoInstanceAgwSmokeTests) is *not* the same path; the ATTACH bridge
/// goes through BPQ's Telnet driver, with its own framing, line
/// discipline, and outbound-TCP semantics.
///
/// These tests speak to BPQ at the byte level (host TCP listener, AGW
/// frame transport on the SUT side). No DAPPS in the loop — the goal is
/// to lock down BPQ's bridge behaviour so a regression here couldn't
/// hide behind DAPPS-level abstractions.
///
/// What's being asserted:
///   - An L2 connect to APPLCALL on B causes BPQ-B to dial our test
///     listener. (i.e. ATTACH actually fires.)
///   - The first bytes BPQ writes are <c>&lt;callsign&gt;\r\n</c>
///     (TelnetV6.c:5774-5775). This is what dapps's <c>ReadLine</c> +
///     <c>.Trim()</c> consumes.
///   - Bytes flow bidirectionally through the bridge.
///   - A clean disconnect from either side propagates.
/// </summary>
[Collection("Linbpq attach-bridge integration")]
[Trait("Category", "Integration")]
public class BpqAttachBridgeTests(TwoInstanceAttachFixture fixture)
{
    // Each test uses a distinct caller SSID so L2 link state from a
    // prior test in the collection can't collide with the next.
    [Fact]
    public async Task InboundLinkToApplCall_TriggersAttachAndWritesCallsignFirstLine()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = ctSource.Token;

        // ── 1. Stand up the host TCP listener BPQ-B will dial ──────────────
        using var attachListener = new TcpListener(IPAddress.Any, fixture.AttachTcpPort);
        attachListener.Start();
        var acceptTask = attachListener.AcceptTcpClientAsync(ct).AsTask();

        // ── 2. Drive an inbound L2 connect from A → ApplCallB via AXIP ────
        var sutCall = fixture.CallsignA + "-1";
        var sutAgw = await OriginateInboundConnect(sutCall, fixture.ApplCallB, ct);

        // ── 3. Wait for BPQ-B to dial us back ─────────────────────────────
        using var bpqClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
        bpqClient.Connected.Should().BeTrue();

        // ── 4. Read the first chunk of bytes BPQ writes ───────────────────
        var stream = bpqClient.GetStream();
        var firstChunk = await ReadAtLeastUntil(stream, terminator: (byte)'\n', cap: 64, ct);

        var firstChunkText = Encoding.ASCII.GetString(firstChunk);
        // BPQ TelnetV6.c:5775 writes "<MyCall>\r\n" — pin both the content
        // and the line termination here. Loose-match the callsign portion
        // (BPQ may upper-case or pass through depending on driver state).
        firstChunkText.Should().EndWith("\r\n",
            "BPQ documents and emits CR-LF; dapps's ReadLine relies on \\n and Trim() removes \\r");
        firstChunkText.TrimEnd('\r', '\n').Should().BeEquivalentTo(sutCall,
            "the first line names the calling station — that's the contract dapps reads");

        await TeardownLink(sutAgw, sutCall, ct);
    }

    [Fact]
    public async Task BridgeFlowsBytesBidirectionally()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = ctSource.Token;

        using var attachListener = new TcpListener(IPAddress.Any, fixture.AttachTcpPort);
        attachListener.Start();
        var acceptTask = attachListener.AcceptTcpClientAsync(ct).AsTask();

        var sutCall = fixture.CallsignA + "-2";
        var sutAgw = await OriginateInboundConnect(sutCall, fixture.ApplCallB, ct);

        using var bpqClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var stream = bpqClient.GetStream();

        // Drain the leading <callsign>\r\n.
        await ReadAtLeastUntil(stream, terminator: (byte)'\n', cap: 64, ct);

        // ── App → user (BPQ → SUT): write to our TCP socket, expect
        //    bytes on the SUT-side AGW connection as 'D' frames. ──
        var greeting = "DAPPSv1>\n"u8.ToArray();
        await stream.WriteAsync(greeting, ct);
        await stream.FlushAsync(ct);

        var agwReceived = await ReadDataFrameContaining(sutAgw.transport, "DAPPSv1>", ct);
        agwReceived.Should().Contain("DAPPSv1>",
            "bytes written by the host listener should reach the SUT through BPQ's bridge");

        // ── User → app (SUT → BPQ → TCP): write a 'D' frame from the AGW
        //    side, expect bytes on the host TCP socket. ──
        await sutAgw.transport.WriteFrameAsync(
            new AgwFrame((byte)fixture.AxipPortIndex, 'D', 0xF0, sutCall, fixture.ApplCallB,
                "ihave abc1234 len=5\n"u8.ToArray()),
            ct);

        var fromSut = await ReadAtLeastUntil(stream, terminator: (byte)'\n', cap: 128, ct);
        Encoding.ASCII.GetString(fromSut).Should().Contain("ihave abc1234",
            "bytes sent by the SUT through BPQ-A → AXIP → BPQ-B → ATTACH should land on the host listener");

        await TeardownLink(sutAgw, sutCall, ct);
    }

    [Fact]
    public async Task ListenerCloseFromDappsSide_PropagatesToL2DisconnectOnSut()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = ctSource.Token;

        using var attachListener = new TcpListener(IPAddress.Any, fixture.AttachTcpPort);
        attachListener.Start();
        var acceptTask = attachListener.AcceptTcpClientAsync(ct).AsTask();

        var sutCall = fixture.CallsignA + "-3";
        var sutAgw = await OriginateInboundConnect(sutCall, fixture.ApplCallB, ct);

        var bpqClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Drain the callsign line.
        await ReadAtLeastUntil(bpqClient.GetStream(), terminator: (byte)'\n', cap: 64, ct);

        // Close the dapps-side socket — same as dapps quitting a session.
        bpqClient.Close();

        // The SUT-side AGW link should see a 'd' (disconnect) frame from
        // BPQ-A within a few seconds. Don't fail if BPQ takes a moment to
        // propagate via AXIP; just bound the wait.
        var sawDisconnect = await TryReadFrame(sutAgw.transport, 'd', TimeSpan.FromSeconds(20), ct);
        sawDisconnect.Should().BeTrue(
            "closing the dapps-side TCP socket must propagate through BPQ-B → AXIP → BPQ-A as an AX.25 disconnect");

        await Task.Delay(1000, ct);
    }

    /// <summary>
    /// Cleanly tear the AX.25 link down so the test's caller-SSID L2 state
    /// at BPQ doesn't linger past test exit. Best-effort — disposal of the
    /// AGW socket also propagates a disconnect, but explicit 'd' is faster
    /// and more deterministic.
    /// </summary>
    private async Task TeardownLink((TcpClient tcp, AgwFrameTransport transport) sutAgw,
        string callFrom, CancellationToken ct)
    {
        try
        {
            await sutAgw.transport.WriteFrameAsync(
                new AgwFrame((byte)fixture.AxipPortIndex, 'd', 0, callFrom, fixture.ApplCallB, []),
                ct);
            await Task.Delay(1000, ct);
        }
        catch { /* best effort */ }
        try { sutAgw.tcp.Dispose(); } catch { /* best effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Open an AGW connection to BPQ-A, register <paramref name="callFrom"/>
    /// and originate a connect to <paramref name="callTo"/> via the AXIP
    /// carrier port. Returns the AGW transport and the underlying TCP
    /// client (caller owns disposal of the latter via the pair's
    /// <c>tcp</c> field, which is kept alive for the duration of the test).
    /// </summary>
    private async Task<(TcpClient tcp, AgwFrameTransport transport)> OriginateInboundConnect(
        string callFrom, string callTo, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(fixture.Host, fixture.AgwPortA, ct);
        var transport = new AgwFrameTransport(tcp.GetStream());

        // Register callfrom so BPQ-A accepts the 'C' from us.
        await transport.WriteFrameAsync(new AgwFrame(0, 'X', 0, callFrom, "", []), ct);
        var xAck = await transport.ReadFrameAsync(ct);
        xAck.Kind.Should().Be('X');

        // 'C' on the AXIP port byte routes via UDP tunnel to BPQ-B's L2,
        // which sees it as an inbound for callTo (= ApplCallB) and fires
        // the APPLICATION+ATTACH wiring.
        await transport.WriteFrameAsync(
            new AgwFrame((byte)fixture.AxipPortIndex, 'C', 0, callFrom, callTo, []),
            ct);

        // Wait for the SUT-side connect-confirm so we know AXIP routing
        // succeeded; otherwise a missing dial-back at the listener could
        // mean either "ATTACH didn't fire" or "the L2 link never formed."
        var confirm = await ReadFrame(transport, 'C', ct);
        Encoding.ASCII.GetString(confirm.Payload).Should().Contain("CONNECTED",
            "AXIP-routed L2 connect must establish before BPQ-B's APPLICATION fires");

        return (tcp, transport);
    }

    private static async Task<byte[]> ReadAtLeastUntil(
        NetworkStream stream, byte terminator, int cap, CancellationToken ct)
    {
        var buf = new List<byte>(cap);
        var one = new byte[1];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && buf.Count < cap)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) break;
            buf.Add(one[0]);
            if (one[0] == terminator) break;
        }
        return buf.ToArray();
    }

    private static async Task<AgwFrame> ReadFrame(AgwFrameTransport transport, char kind, CancellationToken ct)
    {
        while (true)
        {
            var frame = await transport.ReadFrameAsync(ct);
            if (frame.Kind == kind) return frame;
        }
    }

    /// <summary>
    /// Read AGW 'D' frames until one whose payload contains the marker
    /// substring. Useful for asserting "the bytes the listener wrote
    /// arrived on the SUT side" without depending on framing alignment.
    /// </summary>
    private static async Task<string> ReadDataFrameContaining(
        AgwFrameTransport transport, string marker, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await transport.ReadFrameAsync(ct);
            if (frame.Kind != 'D') continue;
            sb.Append(Encoding.UTF8.GetString(frame.Payload));
            if (sb.ToString().Contains(marker)) return sb.ToString();
        }
        throw new TimeoutException($"never saw a 'D' frame containing '{marker}'");
    }

    /// <summary>
    /// Bounded-wait read that returns true iff a frame of the requested
    /// kind arrives before the deadline; swallows the cancellation.
    /// </summary>
    private static async Task<bool> TryReadFrame(
        AgwFrameTransport transport, char kind, TimeSpan timeout, CancellationToken outerCt)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        deadline.CancelAfter(timeout);
        try
        {
            await ReadFrame(transport, kind, deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            return false;
        }
    }
}
