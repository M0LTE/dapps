using System.Net;
using System.Text;
using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Audit-trail tests for the kill-switch poller. The local Stop/Resume
/// button records <c>tx-control</c> rows on every press; this fixture
/// confirms the same audit kind is emitted symmetrically when the
/// remote signal flips.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class TxKillSwitchPollerAuditTests : IAsyncLifetime
{
    private string dbPath = null!;
    private TransmissionAuditService audit = null!;
    private FakeTimeProvider clock = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-ksaudit-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using var c = DbInfo.GetConnection();
        c.CreateTable<DbSystemOption>();
        c.CreateTable<DbTransmission>();

        clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new StubOptions(new SystemOptions
        {
            Callsign = "M0LTE-1",
            TransmissionAuditEnabled = true,
        });
        audit = new TransmissionAuditService(
            NullLogger<TransmissionAuditService>.Instance,
            options,
            mqttBroker: null!,
            timeProvider: clock);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FirstPollAllow_DoesNotAudit()
    {
        var poller = NewPoller(SteadyHandler("""{"txAllowed":true,"reason":"all good","appliesTo":["*"]}"""));
        await poller.RefreshAsync(CancellationToken.None);

        (await CountTxControlRows()).Should().Be(0,
            "the steady-state ALLOW on every startup would be audit noise");
    }

    [Fact]
    public async Task FirstPollBlock_AuditsAsTxControl()
    {
        var poller = NewPoller(SteadyHandler(
            """{"txAllowed":false,"reason":"emergency stop","appliesTo":["*"]}"""));
        await poller.RefreshAsync(CancellationToken.None);

        var rows = await ReadTxControlRows();
        rows.Should().ContainSingle();
        rows[0].Bearer.Should().Be("remote");
        rows[0].Reason.Should().Contain("BLOCK");
        rows[0].Reason.Should().Contain("emergency stop");
    }

    [Fact]
    public async Task TransitionAllowToBlock_AuditsTransition()
    {
        // First call ALLOW (no audit), second call BLOCK (audits).
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":true,"reason":"all good","appliesTo":["*"]}"""),
            Json("""{"txAllowed":false,"reason":"contest QRM","appliesTo":["*"]}"""));
        var poller = NewPoller(sequential);

        await poller.RefreshAsync(CancellationToken.None);
        (await CountTxControlRows()).Should().Be(0);

        await poller.RefreshAsync(CancellationToken.None);
        var rows = await ReadTxControlRows();
        rows.Should().ContainSingle();
        rows[0].Reason.Should().Contain("BLOCK (was ALLOW)");
        rows[0].Reason.Should().Contain("contest QRM");
    }

    [Fact]
    public async Task TransitionBlockToAllow_AuditsTransition()
    {
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":false,"reason":"emergency","appliesTo":["*"]}"""),
            Json("""{"txAllowed":true,"reason":"resolved","appliesTo":["*"]}"""));
        var poller = NewPoller(sequential);

        await poller.RefreshAsync(CancellationToken.None);
        (await CountTxControlRows()).Should().Be(1, "first poll BLOCK audits");

        await poller.RefreshAsync(CancellationToken.None);

        var rows = await ReadTxControlRows();
        rows.Should().HaveCount(2);
        rows[1].Reason.Should().Contain("ALLOW (was BLOCK)");
        rows[1].Reason.Should().Contain("resolved");
    }

    [Fact]
    public async Task SteadyState_DoesNotAuditEachPoll()
    {
        var poller = NewPoller(SteadyHandler(
            """{"txAllowed":false,"reason":"hold","appliesTo":["*"]}"""));

        // Three polls all returning the same BLOCK; only the first
        // should audit.
        await poller.RefreshAsync(CancellationToken.None);
        await poller.RefreshAsync(CancellationToken.None);
        await poller.RefreshAsync(CancellationToken.None);

        (await CountTxControlRows()).Should().Be(1,
            "audit only on transitions, not every steady-state poll");
    }

    [Fact]
    public async Task PollFailure_DoesNotAudit()
    {
        // First poll succeeds (BLOCK -> 1 audit row). Subsequent polls
        // fail; cached state stays. No additional audit rows.
        var sequential = new SequentialHandler(
            Json("""{"txAllowed":false,"reason":"hold","appliesTo":["*"]}"""),
            null,
            null);
        var poller = NewPoller(sequential);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.RefreshAsync(CancellationToken.None);
        await poller.RefreshAsync(CancellationToken.None);

        (await CountTxControlRows()).Should().Be(1, "only the initial BLOCK transition lands");
    }

    [Fact]
    public async Task NotApplicableToCallsign_AuditsAsAllow()
    {
        // URL says BLOCK but appliesTo doesn't match this node. Local
        // gate effectively allows; that's the steady state for this
        // node, so the first poll should not audit.
        var poller = NewPoller(SteadyHandler(
            """{"txAllowed":false,"reason":"GB7RDG only","appliesTo":["GB7RDG-*"]}"""));
        await poller.RefreshAsync(CancellationToken.None);

        (await CountTxControlRows()).Should().Be(0,
            "block targeted elsewhere = ALLOW for us = no audit on first poll");
    }

    private TxKillSwitchPoller NewPoller(HttpMessageHandler handler)
    {
        var options = new StubOptions(new SystemOptions
        {
            Callsign = "M0LTE-1",
            TransmissionAuditEnabled = true,
        });
        return new TxKillSwitchPoller(
            new CannedHttpClientFactory(handler),
            options,
            clock,
            NullLogger<TxKillSwitchPoller>.Instance,
            audit);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpMessageHandler SteadyHandler(string body) =>
        new CannedHandler(_ => Json(body));

    private static async Task<int> CountTxControlRows()
    {
        var conn = DbInfo.GetAsyncConnection();
        var rows = await conn.QueryAsync<DbTransmission>(
            "select * from transmissions where Kind='tx-control';");
        return rows.Count;
    }

    private static async Task<IList<DbTransmission>> ReadTxControlRows()
    {
        var conn = DbInfo.GetAsyncConnection();
        return await conn.QueryAsync<DbTransmission>(
            "select * from transmissions where Kind='tx-control' order by Id;");
    }

    private sealed class StubOptions(SystemOptions value) : IOptionsMonitor<SystemOptions>
    {
        public SystemOptions CurrentValue { get; } = value;
        public SystemOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SystemOptions, string?> listener) => null;
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private sealed class CannedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

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
