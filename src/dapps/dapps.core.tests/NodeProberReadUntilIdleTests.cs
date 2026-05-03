using System.Text;
using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// Plan B6.1 Phase 2b - the node-prompt navigation primitive on
/// <see cref="NodeProber"/> uses <c>ReadUntilIdleAsync</c> to detect
/// "the prompt has finished writing and is now waiting for input"
/// without pattern-matching banner text. The shape: read until the
/// wire goes idle for a short window, treat that as the prompt.
///
/// These tests pin the contract:
///   - empty stream → empty result (idle window never fires; total
///     timeout drains)
///   - data + EOF → returns the data
///   - data + delay + more data → returns data after idle, NOT
///     including the second batch (that's the next reader's job -
///     in the prober, that's <c>ReadInitialPromptAsync</c>)
/// </summary>
public sealed class NodeProberReadUntilIdleTests
{
    [Fact]
    public async Task EmptyStream_ReturnsEmptyAfterTotalTimeout()
    {
        // Total timeout is a hard cap. With nothing in the stream, the
        // first read blocks until the total deadline; we return empty.
        using var stream = new BlockingReadStream();
        var result = await NodeProber.ReadUntilIdleAsync(
            stream,
            totalTimeout: TimeSpan.FromMilliseconds(150),
            idleWindow: TimeSpan.FromMilliseconds(50),
            ct: TestContext.Current.CancellationToken);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DataThenEof_ReturnsDataAndStops()
    {
        var bytes = Encoding.ASCII.GetBytes("Welcome to GB7RDG\r\nREADNG:GB7RDG} ");
        using var stream = new MemoryStream(bytes);
        var result = await NodeProber.ReadUntilIdleAsync(
            stream,
            totalTimeout: TimeSpan.FromSeconds(5),
            idleWindow: TimeSpan.FromMilliseconds(500),
            ct: TestContext.Current.CancellationToken);
        result.Should().Be("Welcome to GB7RDG\r\nREADNG:GB7RDG} ");
    }

    [Fact]
    public async Task DataThenSilenceLongerThanIdleWindow_ReturnsDataWithoutWaitingForMore()
    {
        // Push first chunk, wait past the idle window, push second chunk.
        // ReadUntilIdle should return the FIRST chunk only - the idle
        // window expires before the second arrives, and that's the
        // "prompt is now waiting for input" signal.
        using var stream = new ScriptedStream();
        stream.Push(Encoding.ASCII.GetBytes("BANNER}"));
        var task = NodeProber.ReadUntilIdleAsync(
            stream,
            totalTimeout: TimeSpan.FromSeconds(2),
            idleWindow: TimeSpan.FromMilliseconds(80),
            ct: TestContext.Current.CancellationToken);

        // Give the loop a chance to read the first chunk + start its
        // idle wait. 200 ms is well past the 80 ms idle window.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        stream.Push(Encoding.ASCII.GetBytes("DAPPSv1>"));

        var result = await task;
        result.Should().Be("BANNER}");
    }

    /// <summary>Stream that blocks ReadAsync until cancellation. Used
    /// for the "no data ever arrives" test case.</summary>
    private sealed class BlockingReadStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0; // unreachable
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Stream where reads await externally-pushed chunks. The
    /// test pushes data when it wants the SUT to see it, simulating
    /// the BPQ-on-the-wire pattern where the banner arrives, then
    /// the line goes quiet until the user types something.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _pos;

        public void Push(byte[] data) => _chunks.Writer.TryWrite(data);
        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_current is null || _pos >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct)) return 0;
                if (!_chunks.Reader.TryRead(out _current)) return 0;
                _pos = 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _pos);
            _current.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
