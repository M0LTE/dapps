using System.Text;
using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// Tests for the small but load-bearing helpers in
/// <see cref="Extensions"/>: the receiver-side line reader (used by
/// <c>InboundConnectionHandler</c> to parse <c>ihave</c> commands) and
/// the inactivity-timeout wrapper (used both sides to bound how long
/// a hung peer can wedge a session).
/// </summary>
public class ExtensionsTests
{
    [Fact]
    public async Task ReadLine_StripsTerminatingNewline()
    {
        using var ms = new MemoryStream("ihave abc\n"u8.ToArray());
        var line = await ms.ReadLine(TestContext.Current.CancellationToken);
        line.Should().Be("ihave abc");
    }

    [Fact]
    public async Task ReadLine_ReturnsEmptyOnEof()
    {
        using var ms = new MemoryStream([]);
        (await ms.ReadLine(TestContext.Current.CancellationToken)).Should().Be("");
    }

    [Fact]
    public async Task ReadLine_StopsAtFirstNewlineEvenIfMoreFollows()
    {
        using var ms = new MemoryStream("first\nsecond\n"u8.ToArray());
        (await ms.ReadLine(TestContext.Current.CancellationToken)).Should().Be("first");
        (await ms.ReadLine(TestContext.Current.CancellationToken)).Should().Be("second");
    }

    [Fact]
    public async Task ReadLine_HandlesUtf8MultibyteCharacters()
    {
        var bytes = Encoding.UTF8.GetBytes("héllo wörld\n");
        using var ms = new MemoryStream(bytes);
        (await ms.ReadLine(TestContext.Current.CancellationToken)).Should().Be("héllo wörld");
    }

    [Fact]
    public async Task WithInactivityTimeout_OperationCompletes_ReturnsValue()
    {
        var result = await Extensions.WithInactivityTimeout(
            ct => Task.FromResult(42),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        result.Should().Be(42);
    }

    [Fact]
    public async Task WithInactivityTimeout_OperationHangs_TimesOutWithCancel()
    {
        var act = async () => await Extensions.WithInactivityTimeout(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; },
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WithInactivityTimeout_VoidOverload_AlsoTimesOut()
    {
        var act = async () => await Extensions.WithInactivityTimeout(
            async ct => await Task.Delay(Timeout.Infinite, ct),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WithInactivityTimeout_OuterCancellation_PropagatesAhead()
    {
        using var outer = new CancellationTokenSource();
        var op = async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return 1;
        };
        var task = Extensions.WithInactivityTimeout(op, TimeSpan.FromSeconds(60), outer.Token);

        outer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
