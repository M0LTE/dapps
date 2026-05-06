using System.Net;
using System.Text;
using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// PR 3: poller behaviour. Covers the JSON contract, callsign
/// targeting, fail-open / fail-closed staleness, and the
/// ITxKillSwitchSignal surface the gate consumes.
/// </summary>
public class TxKillSwitchPollerTests
{
    private sealed class StubOptions(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions Value { get; set; } = value;
        public SystemOptions CurrentValue => Value;
        public SystemOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class CannedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Disabled_WhenUrlEmpty_NeverFetchesAndAllows()
    {
        var calls = 0;
        var handler = new CannedHandler(_ => { calls++; return Json("{}"); });
        var opts = new StubOptions(new SystemOptions { TxKillSwitchUrl = "", Callsign = "M0LTE-1" });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        calls.Should().Be(0);
        poller.RemoteAllowed.Should().BeTrue();
        poller.RemoteBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task Allowed_WhenResponseSaysSo()
    {
        var handler = new CannedHandler(_ => Json("""{"txAllowed":true,"reason":"normal ops","appliesTo":["*"]}"""));
        var opts = new StubOptions(new SystemOptions { TxKillSwitchUrl = "http://example/ks", Callsign = "M0LTE-1" });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue();
        poller.RemoteBlockReason.Should().BeNull();
        poller.LastSuccessAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Blocked_WhenResponseSaysSo_AndAppliesToThisCallsign()
    {
        var handler = new CannedHandler(_ => Json(
            """{"txAllowed":false,"reason":"contest QRM","appliesTo":["M0LTE-*"]}"""));
        var opts = new StubOptions(new SystemOptions { TxKillSwitchUrl = "http://example/ks", Callsign = "M0LTE-2" });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeFalse();
        poller.RemoteBlockReason.Should().Contain("contest QRM");
    }

    [Fact]
    public async Task Allowed_WhenBlockedButCallsignNotInAppliesTo()
    {
        // Block targeted at GB7RDG only; we are M0LTE-2 - should not gag us.
        var handler = new CannedHandler(_ => Json(
            """{"txAllowed":false,"reason":"GB7RDG site QRM","appliesTo":["GB7RDG-*"]}"""));
        var opts = new StubOptions(new SystemOptions { TxKillSwitchUrl = "http://example/ks", Callsign = "M0LTE-2" });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue();
        poller.RemoteBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task Allowed_WhenAppliesToOmittedAndTxAllowedTrue()
    {
        var handler = new CannedHandler(_ => Json("""{"txAllowed":true,"reason":"all good"}"""));
        var opts = new StubOptions(new SystemOptions { TxKillSwitchUrl = "http://example/ks", Callsign = "M0LTE-1" });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Stale_FailOpen_AllowsAfterStalenessElapses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // Initially blocked.
        var handler = new CannedHandler(_ => Json("""{"txAllowed":false,"reason":"emergency","appliesTo":["*"]}"""));
        var opts = new StubOptions(new SystemOptions
        {
            TxKillSwitchUrl = "http://example/ks",
            Callsign = "M0LTE-1",
            TxKillSwitchStaleSeconds = 60,
            TxKillSwitchFailOpen = true,
        });
        var poller = NewPoller(handler, opts, clock);

        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeFalse("fresh successful poll said block");

        // Now the publisher goes silent. Switch the handler to throw on
        // every call, and let time march past the staleness window.
        handler = new CannedHandler(_ => throw new HttpRequestException("network down"));
        var poller2 = NewPoller(handler, opts, clock);

        // Hydrate poller2 by replaying the original successful state.
        // Simpler: have it poll once successfully then go dark.
        var successThenFail = new SequentialHandler(
            Json("""{"txAllowed":false,"reason":"emergency","appliesTo":["*"]}"""),
            null);  // null = throw on subsequent calls
        var pollerSeq = NewPoller(successThenFail, opts, clock);
        await pollerSeq.RefreshAsync(CancellationToken.None);
        pollerSeq.RemoteAllowed.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(120));  // past staleness
        await pollerSeq.RefreshAsync(CancellationToken.None);  // fails
        pollerSeq.RemoteAllowed.Should().BeTrue("fail-open + stale = allow");
        pollerSeq.RemoteBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task Stale_FailClosed_BlocksAfterStalenessElapses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":true,"reason":"normal","appliesTo":["*"]}"""),
            null);
        var opts = new StubOptions(new SystemOptions
        {
            TxKillSwitchUrl = "http://example/ks",
            Callsign = "M0LTE-1",
            TxKillSwitchStaleSeconds = 60,
            TxKillSwitchFailOpen = false,
        });
        var poller = NewPoller(sequential, opts, clock);

        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(120));
        await poller.RefreshAsync(CancellationToken.None);  // fails

        poller.RemoteAllowed.Should().BeFalse("fail-closed + stale = block");
        poller.RemoteBlockReason.Should().Contain("unreachable");
    }

    [Fact]
    public async Task NeverFetchedSuccessfully_FailOpen_Allows()
    {
        var handler = new CannedHandler(_ => throw new HttpRequestException("dns down"));
        var opts = new StubOptions(new SystemOptions
        {
            TxKillSwitchUrl = "http://example/ks",
            Callsign = "M0LTE-1",
            TxKillSwitchFailOpen = true,
        });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue("fail-open default allows when bootstrap fails");
        poller.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task NeverFetchedSuccessfully_FailClosed_Blocks()
    {
        var handler = new CannedHandler(_ => throw new HttpRequestException("dns down"));
        var opts = new StubOptions(new SystemOptions
        {
            TxKillSwitchUrl = "http://example/ks",
            Callsign = "M0LTE-1",
            TxKillSwitchFailOpen = false,
        });
        var poller = NewPoller(handler, opts, new FakeTimeProvider());

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeFalse();
        poller.RemoteBlockReason.Should().Contain("unreachable");
    }

    [Fact]
    public async Task TransientFailure_KeepsLastSuccessfulValue_WithinStalenessWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":false,"reason":"licence inspection","appliesTo":["*"]}"""),
            null,  // network glitch
            null); // still down
        var opts = new StubOptions(new SystemOptions
        {
            TxKillSwitchUrl = "http://example/ks",
            Callsign = "M0LTE-1",
            TxKillSwitchStaleSeconds = 600,  // 10min staleness
        });
        var poller = NewPoller(sequential, opts, clock);

        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(30));
        await poller.RefreshAsync(CancellationToken.None);   // fails

        poller.RemoteAllowed.Should().BeFalse("within staleness window, keep last known block");
        poller.RemoteBlockReason.Should().Contain("licence inspection");

        clock.Advance(TimeSpan.FromSeconds(30));
        await poller.RefreshAsync(CancellationToken.None);   // fails again
        poller.RemoteAllowed.Should().BeFalse("still within window");
    }

    [Theory]
    [InlineData(new string[] { "*" }, "M0LTE-1", true)]
    [InlineData(new string[] { "*" }, "GB7RDG", true)]
    [InlineData(new string[] { "M0LTE-*" }, "M0LTE-1", true)]
    [InlineData(new string[] { "M0LTE-*" }, "m0lte-9", true)]            // case-insensitive
    [InlineData(new string[] { "M0LTE-*" }, "GB7RDG", false)]
    [InlineData(new string[] { "M0LTE-2" }, "M0LTE-2", true)]
    [InlineData(new string[] { "M0LTE-2" }, "M0LTE-3", false)]
    [InlineData(new string[] { "M0LTE-*", "GB7RDG" }, "GB7RDG", true)]   // multi-pattern
    [InlineData(new string[] { }, "M0LTE-1", true)]                      // empty list = all
    [InlineData(new string[] { "" }, "M0LTE-1", false)]                  // explicit empty entry skipped
    public void AppliesToThisNode_PatternMatchesExpected(string[] patterns, string callsign, bool expected)
    {
        TxKillSwitchPoller.AppliesToThisNode(patterns, callsign).Should().Be(expected);
    }

    private static TxKillSwitchPoller NewPoller(
        HttpMessageHandler handler,
        IOptionsMonitor<SystemOptions> options,
        TimeProvider clock)
    {
        return new TxKillSwitchPoller(
            new CannedHttpClientFactory(handler),
            options,
            clock,
            NullLogger<TxKillSwitchPoller>.Instance);
    }

    /// <summary>Returns each prepared response in order, then null
    /// entries make subsequent SendAsync throw HttpRequestException.</summary>
    private sealed class SequentialHandler(params HttpResponseMessage?[] responses) : HttpMessageHandler
    {
        private int idx;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage? r = idx < responses.Length ? responses[idx] : null;
            idx++;
            if (r is null) throw new HttpRequestException("planned-fault");
            return Task.FromResult(r);
        }
    }
}
