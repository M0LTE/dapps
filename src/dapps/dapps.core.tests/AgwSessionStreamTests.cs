using dapps.client.Transport.Agw;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

public class AgwSessionStreamTests
{
    private static AgwSessionStream OverStream(Stream underlying)
    {
        // The session stream is `internal`, so this test project relies on
        // InternalsVisibleTo (granted by a friendlier test surface). For
        // now construct via the Agw namespace directly.
        var transport = new AgwFrameTransport(underlying);
        return new AgwSessionStream(transport, port: 0, callfrom: "ME", callto: "YOU", NullLogger.Instance);
    }

    [Fact]
    public async Task ReadAsync_ReturnsBytesFromSingleDFrame()
    {
        var ms = new MemoryStream();
        ms.Write(new AgwFrame(0, 'D', 0xF0, "YOU", "ME", [1, 2, 3]).ToBytes());
        ms.Position = 0;

        var stream = OverStream(ms);
        var buf = new byte[10];

        var n = await stream.ReadAsync(buf);

        n.Should().Be(3);
        buf.AsSpan(0, n).ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ReadAsync_AcrossMultipleFrames_BuffersCorrectly()
    {
        var ms = new MemoryStream();
        ms.Write(new AgwFrame(0, 'D', 0xF0, "YOU", "ME", [1, 2, 3]).ToBytes());
        ms.Write(new AgwFrame(0, 'D', 0xF0, "YOU", "ME", [4, 5]).ToBytes());
        ms.Position = 0;

        var stream = OverStream(ms);

        var first = new byte[10];
        var nFirst = await stream.ReadAsync(first);
        nFirst.Should().Be(3);

        var second = new byte[10];
        var nSecond = await stream.ReadAsync(second);
        nSecond.Should().Be(2);
        second.AsSpan(0, nSecond).ToArray().Should().Equal(4, 5);
    }

    [Fact]
    public async Task ReadAsync_BufferSmallerThanFrame_ReturnsLeftoverOnNextCall()
    {
        var ms = new MemoryStream();
        ms.Write(new AgwFrame(0, 'D', 0xF0, "YOU", "ME", [1, 2, 3, 4, 5]).ToBytes());
        ms.Position = 0;

        var stream = OverStream(ms);

        var first = new byte[2];
        (await stream.ReadAsync(first)).Should().Be(2);
        first.Should().Equal(1, 2);

        var second = new byte[10];
        var nSecond = await stream.ReadAsync(second);
        nSecond.Should().Be(3);
        second.AsSpan(0, nSecond).ToArray().Should().Equal(3, 4, 5);
    }

    [Fact]
    public async Task ReadAsync_DowncaseDFrame_SignalsEof()
    {
        var ms = new MemoryStream();
        ms.Write(new AgwFrame(0, 'd', 0xF0, "YOU", "ME", []).ToBytes());
        ms.Position = 0;

        var stream = OverStream(ms);
        var buf = new byte[10];

        (await stream.ReadAsync(buf)).Should().Be(0);
        (await stream.ReadAsync(buf)).Should().Be(0); // sticky
    }

    [Fact]
    public async Task ReadAsync_SkipsFramesThatArentDord()
    {
        var ms = new MemoryStream();
        // Some monitor traffic the connect path would also have to skip
        ms.Write(new AgwFrame(0, 'U', 0xF0, "FOO", "BAR", [99]).ToBytes());
        ms.Write(new AgwFrame(0, 'D', 0xF0, "YOU", "ME", [42]).ToBytes());
        ms.Position = 0;

        var stream = OverStream(ms);
        var buf = new byte[10];

        var n = await stream.ReadAsync(buf);
        n.Should().Be(1);
        buf[0].Should().Be(42);
    }

    [Fact]
    public async Task WriteAsync_EmitsDFrameWithExpectedFields()
    {
        var ms = new MemoryStream();
        var stream = OverStream(ms);

        await stream.WriteAsync(new byte[] { 1, 2, 3 });

        ms.Position = 0;
        var framing = new AgwFrameTransport(ms);
        var frame = await framing.ReadFrameAsync(CancellationToken.None);

        frame.Kind.Should().Be('D');
        frame.Port.Should().Be(0);
        frame.Pid.Should().Be(0xF0);
        frame.CallFrom.Should().Be("ME");
        frame.CallTo.Should().Be("YOU");
        frame.Payload.Should().Equal(1, 2, 3);
    }
}
