using System.Text;
using AwesomeAssertions;
using dapps.client.Transport.Agw;

namespace dapps.core.tests;

/// <summary>
/// Unit tests for the multiplexed AGW session stream - the inbound-side
/// glue that lets <c>InboundConnectionHandler</c> consume bytes from a
/// shared AGW socket as if it had a private duplex stream.
/// </summary>
public class MultiplexedAgwSessionStreamTests
{
    [Fact]
    public async Task PushedBytes_AreReadableInOrder()
    {
        var sent = new List<byte[]>();
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (data, _) => { sent.Add(data); return Task.CompletedTask; },
            sendRemoteDisconnect: _ => Task.CompletedTask);

        await stream.PushIncoming("hello "u8.ToArray(), TestContext.Current.CancellationToken);
        await stream.PushIncoming("world\n"u8.ToArray(), TestContext.Current.CancellationToken);

        var buf = new byte[64];
        var n = await stream.ReadAsync(buf.AsMemory(), TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("hello world\n");
    }

    [Fact]
    public async Task Read_BlocksUntilPushArrives_ThenReturns()
    {
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => Task.CompletedTask);

        var ct = TestContext.Current.CancellationToken;
        var buf = new byte[16];
        var read = stream.ReadAsync(buf.AsMemory(), ct).AsTask();
        read.IsCompleted.Should().BeFalse("no bytes pushed yet");

        await stream.PushIncoming("abc"u8.ToArray(), ct);
        var n = await read.WaitAsync(TimeSpan.FromSeconds(1), ct);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("abc");
    }

    [Fact]
    public async Task SignalRemoteDisconnect_DrainsBufferThenReturnsZero()
    {
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => Task.CompletedTask);

        var ct = TestContext.Current.CancellationToken;
        await stream.PushIncoming("trailing"u8.ToArray(), ct);
        stream.SignalRemoteDisconnect();

        var buf = new byte[16];
        var n = await stream.ReadAsync(buf.AsMemory(), ct);
        Encoding.UTF8.GetString(buf, 0, n).Should().Be("trailing",
            "bytes pushed before disconnect must still be readable");

        var n2 = await stream.ReadAsync(buf.AsMemory(), ct);
        n2.Should().Be(0, "subsequent reads see EOF after the buffer drains");
    }

    [Fact]
    public async Task Write_InvokesCallbackWithExactBytes()
    {
        var sent = new List<byte[]>();
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (data, _) => { sent.Add(data); return Task.CompletedTask; },
            sendRemoteDisconnect: _ => Task.CompletedTask);

        await stream.WriteAsync("DAPPSv1>\n"u8.ToArray().AsMemory(),
            TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        sent.Should().HaveCount(1);
        Encoding.UTF8.GetString(sent[0]).Should().Be("DAPPSv1>\n");
    }

    [Fact]
    public async Task DisposeAsync_FiresRemoteDisconnectCallback()
    {
        var disconnectFired = false;
        var stream = new MultiplexedAgwSessionStream(
            writeOutgoing: (_, _) => Task.CompletedTask,
            sendRemoteDisconnect: _ => { disconnectFired = true; return Task.CompletedTask; });

        await stream.DisposeAsync();
        disconnectFired.Should().BeTrue(
            "disposing the stream should signal a 'd' frame so BPQ tears down the L2 link rather than leaving it half-up");
    }
}
