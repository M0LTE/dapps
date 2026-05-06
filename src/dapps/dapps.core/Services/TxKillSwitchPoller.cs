using System.Net.Http.Json;
using System.Text.Json.Serialization;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Centralised TX kill-switch implementation of
/// <see cref="ITxKillSwitchSignal"/>. Polls
/// <see cref="SystemOptions.TxKillSwitchUrl"/> on a configurable
/// cadence and exposes the current allow/deny state to the
/// <see cref="SystemOptionsBackedTxGate"/>.
///
/// Wire shape (small, easy to host on a static gist / S3 / status
/// page / cluster admin endpoint):
/// <code>
/// { "txAllowed": true, "reason": "normal ops", "appliesTo": ["*"] }
/// </code>
/// <c>appliesTo</c> contains glob-ish callsign patterns (one or more)
/// matched case-insensitively against this node's callsign:
/// <list type="bullet">
/// <item><c>"*"</c> matches every node.</item>
/// <item><c>"M0LTE-*"</c> matches every SSID under <c>M0LTE</c>.</item>
/// <item><c>"M0LTE-2"</c> matches exactly that callsign.</item>
/// </list>
/// If the response excludes us we treat it as "not gated for this
/// node" - so one URL can stop one site without affecting the rest.
///
/// Fail modes:
/// <list type="bullet">
/// <item><b>HTTP / parse failure</b> - keeps the last successful state.
/// Once <see cref="SystemOptions.TxKillSwitchStaleSeconds"/> has
/// elapsed since the last good fetch, switches to the
/// <see cref="SystemOptions.TxKillSwitchFailOpen"/> behaviour.</item>
/// <item><b>URL empty</b> - poller idles; remote signal is open.</item>
/// </list>
///
/// Errors are swallowed by design: a poller crash mustn't take down
/// the daemon. Same posture as <see cref="UpdateChecker"/>.
/// </summary>
public sealed class TxKillSwitchPoller(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<TxKillSwitchPoller> logger) : BackgroundService, ITxKillSwitchSignal
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    private readonly object stateLock = new();
    private bool lastTxAllowed = true;
    private string? lastReason;
    private DateTimeOffset? lastSuccessAt;
    private string? lastError;

    public bool RemoteAllowed
    {
        get
        {
            lock (stateLock)
            {
                var opts = options.CurrentValue;
                if (string.IsNullOrWhiteSpace(opts.TxKillSwitchUrl))
                {
                    // Poller is disabled - no remote signal in play.
                    return true;
                }
                if (lastSuccessAt is { } when_ && IsStale(when_, opts))
                {
                    // Cached value is older than the staleness window.
                    // Apply the configured fail-open / fail-closed rule.
                    return opts.TxKillSwitchFailOpen;
                }
                if (lastSuccessAt is null)
                {
                    // Never fetched successfully (poller hasn't run, or
                    // every attempt has failed since startup). Same
                    // fail-open / fail-closed call as a stale cache.
                    return opts.TxKillSwitchFailOpen;
                }
                return lastTxAllowed;
            }
        }
    }

    public string? RemoteBlockReason
    {
        get
        {
            lock (stateLock)
            {
                var opts = options.CurrentValue;
                if (string.IsNullOrWhiteSpace(opts.TxKillSwitchUrl)) return null;
                if (RemoteAllowed) return null;
                if (lastSuccessAt is null)
                {
                    return $"remote kill-switch unreachable ({lastError ?? "no successful poll yet"}); fail-closed by config";
                }
                if (lastSuccessAt is { } when_ && IsStale(when_, opts))
                {
                    return $"remote kill-switch unreachable since {when_.UtcDateTime:s}Z; fail-closed by config";
                }
                return string.IsNullOrWhiteSpace(lastReason)
                    ? "remote kill-switch active"
                    : $"remote: {lastReason}";
            }
        }
    }

    /// <summary>Test seam: the last successful poll timestamp, or null
    /// if we've never had one.</summary>
    public DateTimeOffset? LastSuccessAt
    {
        get { lock (stateLock) return lastSuccessAt; }
    }

    /// <summary>Test seam: the most recent error message, if any.</summary>
    public string? LastError
    {
        get { lock (stateLock) return lastError; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnce(stoppingToken);

            var opts = options.CurrentValue;
            var delaySeconds = Math.Max(5, opts.TxKillSwitchPollSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Trigger a fresh poll immediately. Used by tests and a
    /// future "force re-check" admin button.</summary>
    public Task RefreshAsync(CancellationToken ct) => PollOnce(ct);

    private async Task PollOnce(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var url = opts.TxKillSwitchUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // Disabled: clear any stale state so a flip-on later starts
            // clean rather than reviving a months-old cached "blocked".
            lock (stateLock)
            {
                lastTxAllowed = true;
                lastReason = null;
                lastSuccessAt = null;
                lastError = null;
            }
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("tx-kill-switch");
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetFromJsonAsync<KillSwitchResponse>(url, ct);
            if (response is null)
            {
                StashError("empty response body");
                return;
            }

            var localCallsign = opts.Callsign ?? "";
            var applies = AppliesToThisNode(response.AppliesTo, localCallsign);
            var allowed = !applies || response.TxAllowed;
            var reason = applies ? response.Reason : null;

            lock (stateLock)
            {
                lastTxAllowed = allowed;
                lastReason = reason;
                lastSuccessAt = timeProvider.GetUtcNow();
                lastError = null;
            }

            logger.LogInformation(
                "TX kill-switch: {0} (txAllowed={1}, applies={2}, reason='{3}')",
                allowed ? "ALLOW" : "BLOCK", response.TxAllowed, applies, reason ?? "");
        }
        catch (Exception ex)
        {
            StashError(ex.Message);
            logger.LogDebug(ex, "TX kill-switch poll failed");
        }
    }

    private void StashError(string message)
    {
        lock (stateLock) lastError = message;
    }

    private bool IsStale(DateTimeOffset successAt, SystemOptions opts)
    {
        var now = timeProvider.GetUtcNow();
        return now - successAt > TimeSpan.FromSeconds(Math.Max(30, opts.TxKillSwitchStaleSeconds));
    }

    /// <summary>True when this node's callsign matches any pattern in
    /// the response. Empty / null pattern list = applies to everyone
    /// (matches the natural default for an "everyone stop" kill-switch).</summary>
    public static bool AppliesToThisNode(IReadOnlyList<string>? patterns, string callsign)
    {
        if (patterns is null || patterns.Count == 0) return true;
        foreach (var raw in patterns)
        {
            var p = raw?.Trim() ?? "";
            if (p.Length == 0) continue;
            if (p == "*") return true;
            if (p.EndsWith('*'))
            {
                var prefix = p[..^1];
                if (callsign.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (string.Equals(p, callsign, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed class KillSwitchResponse
    {
        [JsonPropertyName("txAllowed")] public bool TxAllowed { get; set; } = true;
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("appliesTo")] public List<string>? AppliesTo { get; set; }
    }
}
