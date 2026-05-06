using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Text;

namespace dapps.core.tests;

/// <summary>
/// Poller behaviour. The URL, poll cadence, staleness window, and
/// fail-open mode are hardcoded constants on
/// <see cref="TxKillSwitchPoller"/>; the canned <c>HttpMessageHandler</c>
/// here intercepts every outbound HTTP call regardless of URL, so
/// these tests exercise behaviour without caring what the production
/// URL is.
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
    public async Task Allowed_WhenResponseSaysSo()
    {
        var handler = new CannedHandler(_ => Json("""{"txAllowed":true,"reason":"normal ops","appliesTo":["*"]}"""));
        var poller = NewPoller(handler, new SystemOptions { Callsign = "M0LTE-1" });

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
        var poller = NewPoller(handler, new SystemOptions { Callsign = "M0LTE-2" });

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeFalse();
        poller.RemoteBlockReason.Should().Contain("contest QRM");
        poller.RemoteBlockReason.Should().Contain("dev-time kill-switch");
    }

    [Fact]
    public async Task Allowed_WhenBlockedButCallsignNotInAppliesTo()
    {
        // Block targeted at GB7RDG only; we are M0LTE-2 - should not gag us.
        var handler = new CannedHandler(_ => Json(
            """{"txAllowed":false,"reason":"GB7RDG site QRM","appliesTo":["GB7RDG-*"]}"""));
        var poller = NewPoller(handler, new SystemOptions { Callsign = "M0LTE-2" });

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue();
        poller.RemoteBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task Allowed_WhenAppliesToOmittedAndTxAllowedTrue()
    {
        var handler = new CannedHandler(_ => Json("""{"txAllowed":true,"reason":"all good"}"""));
        var poller = NewPoller(handler, new SystemOptions { Callsign = "M0LTE-1" });

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task NeverFetchedSuccessfully_FailOpen_Allows()
    {
        // Production constant: FailOpen=true. A node with no internet
        // at boot (or behind a proxy that's not yet up) keeps emitting
        // rather than going silent.
        var handler = new CannedHandler(_ => throw new HttpRequestException("dns down"));
        var poller = NewPoller(handler, new SystemOptions { Callsign = "M0LTE-1" });

        await poller.RefreshAsync(CancellationToken.None);

        poller.RemoteAllowed.Should().BeTrue();
        poller.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task TransientFailure_KeepsLastSuccessfulValue_WithinStalenessWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // First call: blocked. Subsequent calls: planned-fault.
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":false,"reason":"licence inspection","appliesTo":["*"]}"""),
            null,
            null);
        var poller = NewPoller(sequential, new SystemOptions { Callsign = "M0LTE-1" }, clock);

        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeFalse();

        // Bump clock 30s, attempt a refresh that fails. Still inside
        // the 600s staleness window, so the cached "blocked" sticks.
        clock.Advance(TimeSpan.FromSeconds(30));
        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeFalse();
        poller.RemoteBlockReason.Should().Contain("licence inspection");

        clock.Advance(TimeSpan.FromSeconds(30));
        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeFalse("still within window");
    }

    [Fact]
    public async Task Stale_FallsBackToFailOpen()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":false,"reason":"emergency","appliesTo":["*"]}"""),
            null);
        var poller = NewPoller(sequential, new SystemOptions { Callsign = "M0LTE-1" }, clock);

        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeFalse("fresh successful poll said block");

        // Advance past the hardcoded 600s staleness window. Subsequent
        // refresh fails. With FailOpen=true (production constant), we
        // re-allow.
        clock.Advance(TimeSpan.FromSeconds(TxKillSwitchPoller.StaleSeconds + 60));
        await poller.RefreshAsync(CancellationToken.None);
        poller.RemoteAllowed.Should().BeTrue("stale + fail-open = allow");
        poller.RemoteBlockReason.Should().BeNull();
    }

    [Theory]
    [InlineData(new string[] { "*" }, "M0LTE-1", true)]
    [InlineData(new string[] { "*" }, "GB7RDG", true)]
    [InlineData(new string[] { "M0LTE-*" }, "M0LTE-1", true)]
    [InlineData(new string[] { "M0LTE-*" }, "m0lte-9", true)]            // case-insensitive
    [InlineData(new string[] { "M0LTE-*" }, "GB7RDG", false)]
    [InlineData(new string[] { "M0LTE-2" }, "M0LTE-2", true)]
    [InlineData(new string[] { "M0LTE-2" }, "M0LTE-3", false)]
    [InlineData(new string[] { "M0LTE-*", "GB7RDG" }, "GB7RDG", true)]
    [InlineData(new string[] { }, "M0LTE-1", true)]
    [InlineData(new string[] { "" }, "M0LTE-1", false)]
    public void AppliesToThisNode_PatternMatchesExpected(string[] patterns, string callsign, bool expected)
    {
        TxKillSwitchPoller.AppliesToThisNode(patterns, callsign).Should().Be(expected);
    }

    [Fact]
    public void HardcodedConstants_PinProductionValues()
    {
        // Pin the production constants so an accidental tweak shows
        // up as a failing test review item, not a silent change to
        // safety-critical defaults.
        TxKillSwitchPoller.KillSwitchUrl.Should().Be(
            "https://compute.oarc.uk/storage/public/folders/4803/dapps-devtime-killswitch.json");
        TxKillSwitchPoller.PollSeconds.Should().Be(300);
        TxKillSwitchPoller.StaleSeconds.Should().Be(1800);
        TxKillSwitchPoller.FailOpen.Should().BeTrue();
    }

    private static TxKillSwitchPoller NewPoller(
        HttpMessageHandler handler,
        SystemOptions options,
        TimeProvider? clock = null)
    {
        return new TxKillSwitchPoller(
            new CannedHttpClientFactory(handler),
            new StubOptions(options),
            clock ?? new FakeTimeProvider(),
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
