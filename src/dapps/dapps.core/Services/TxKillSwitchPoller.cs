using System.Net.Http.Json;
using System.Text.Json.Serialization;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Centralised TX kill-switch implementation of
/// <see cref="ITxKillSwitchSignal"/>. Polls a single hardcoded URL
/// (controlled by the project author during the development phase)
/// and exposes the current allow/deny state to the
/// <see cref="SystemOptionsBackedTxGate"/>.
///
/// <para><b>Deliberately not configurable.</b> The URL, poll cadence,
/// staleness window, and fail-open behaviour are constants. Operators
/// running pre-1.0 DAPPS cannot disable it, repoint it, or relax the
/// timings. The mechanism is a development-phase safety net so the
/// author can ask experimental nodes to pause if a release ships with
/// a bug that transmits more than it should. It will be removed (or
/// made genuinely configurable per-fleet) before 1.0. See
/// <c>docs/dev-time-tx-kill-switch.md</c> for the operator-facing
/// rationale.</para>
///
/// Wire shape (small, easy to host on a static gist / S3 / status
/// page):
/// <code>
/// { "txAllowed": true, "reason": "normal ops", "appliesTo": ["*"] }
/// </code>
/// <c>appliesTo</c> contains glob-ish callsign patterns matched
/// case-insensitively against this node's callsign. <c>"*"</c>
/// matches every node; <c>"M0LTE-*"</c> matches every SSID under
/// <c>M0LTE</c>; <c>"M0LTE-2"</c> matches exactly that callsign.
/// If the response excludes us we treat it as "not gated for this
/// node".
///
/// Fail behaviour: HTTP / parse failure keeps the last successful
/// state for <see cref="StaleSeconds"/>, then falls back to allow
/// (fail-open). A network outage doesn't silence a working amateur
/// radio installation - the kill-switch is for active intervention
/// by the project author, not graceful degradation.
///
/// Errors are swallowed by design: a poller crash mustn't take down
/// the daemon. Same posture as <see cref="UpdateChecker"/>.
/// </summary>
public sealed class TxKillSwitchPoller(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<TxKillSwitchPoller> logger,
    TransmissionAuditService? audit = null) : BackgroundService, ITxKillSwitchSignal
{
    /// <summary>
    /// Hardcoded kill-switch URL controlled by the DAPPS project
    /// author (M0LTE) during the development phase. See class summary
    /// and <c>docs/dev-time-tx-kill-switch.md</c>.
    /// </summary>
    public const string KillSwitchUrl =
        "https://compute.oarc.uk/storage/public/folders/4803/dapps-devtime-killswitch.json";

    /// <summary>Seconds between polls. Constant: cannot be tuned per
    /// node. 300s (5 min) is responsive enough to pause a misbehaving
    /// fleet within a single beacon cadence, slow enough that the
    /// outbound traffic to the publishing endpoint stays trivial
    /// even at hundreds of nodes.</summary>
    public const int PollSeconds = 300;

    /// <summary>Seconds without a successful fetch before the cached
    /// value is considered stale. Constant: 1800 (30 min) - several
    /// poll cycles of failure before declaring the cached value
    /// untrustworthy, long enough to ride out a transient outage,
    /// short enough that a stuck poller stops trusting hour-old state.</summary>
    public const int StaleSeconds = 1800;

    /// <summary>When stale or never-yet-fetched, allow TX. Constant:
    /// the kill-switch is for active intervention, not for silencing
    /// nodes that lose internet.</summary>
    public const bool FailOpen = true;

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
                if (lastSuccessAt is { } when_ && IsStale(when_))
                {
                    return FailOpen;
                }
                if (lastSuccessAt is null)
                {
                    return FailOpen;
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
                if (RemoteAllowed) return null;
                // RemoteAllowed=false implies FailOpen is false (it's
                // not, in production) OR we have a fresh non-stale
                // block. Cover both cleanly.
                if (lastSuccessAt is null)
                {
                    return $"dev-time kill-switch unreachable ({lastError ?? "no successful poll yet"}); fail-closed by config";
                }
                if (lastSuccessAt is { } when_ && IsStale(when_))
                {
                    return $"dev-time kill-switch unreachable since {when_.UtcDateTime:s}Z; fail-closed by config";
                }
                return string.IsNullOrWhiteSpace(lastReason)
                    ? "dev-time kill-switch active"
                    : $"dev-time kill-switch: {lastReason}";
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

            try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Trigger a fresh poll immediately. Used by tests.</summary>
    public Task RefreshAsync(CancellationToken ct) => PollOnce(ct);

    private async Task PollOnce(CancellationToken ct)
    {
        try
        {
            // Belt-and-braces: refuse to fetch over plaintext even if
            // the constant ever drifts. Cert validation itself is
            // done by the named HttpClient's primary handler (see DI
            // registration in Program.cs), which uses a fresh
            // SocketsHttpHandler with default SslOptions - the system
            // trust store + chain/hostname/expiry checks. We never
            // override RemoteCertificateValidationCallback anywhere
            // in the process; this guard catches the URL-scheme
            // angle that's not in the handler's job description.
            if (!KillSwitchUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "TX kill-switch URL must be https:// - refusing to poll over plaintext");
            }

            var client = httpClientFactory.CreateClient("tx-kill-switch");
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetFromJsonAsync<KillSwitchResponse>(KillSwitchUrl, ct);
            if (response is null)
            {
                StashError("empty response body");
                return;
            }

            var localCallsign = options.CurrentValue.Callsign ?? "";
            var applies = AppliesToThisNode(response.AppliesTo, localCallsign);
            var allowed = !applies || response.TxAllowed;
            var reason = applies ? response.Reason : null;

            bool previousAllowed;
            DateTimeOffset? previousSuccess;
            lock (stateLock)
            {
                previousAllowed = lastTxAllowed;
                previousSuccess = lastSuccessAt;

                lastTxAllowed = allowed;
                lastReason = reason;
                lastSuccessAt = timeProvider.GetUtcNow();
                lastError = null;
            }

            logger.LogInformation(
                "TX kill-switch: {0} (txAllowed={1}, applies={2}, reason='{3}')",
                allowed ? "ALLOW" : "BLOCK", response.TxAllowed, applies, reason ?? "");

            // Audit operator-visible transitions: the local Stop/Resume
            // button writes tx-control rows already; we mirror that
            // here for remote-driven changes so audit history shows
            // *both* signals symmetrically. Skip the first-poll-says-
            // ALLOW case to avoid one tx-control row per startup; the
            // first-poll-says-BLOCK case is genuinely notable and gets
            // a row.
            if (audit is not null)
            {
                if (previousSuccess is null && !allowed)
                {
                    await audit.RecordAsync(
                        kind: "tx-control",
                        bearer: "remote",
                        reason: $"dev-time kill-switch BLOCK on first poll: {reason ?? "(no reason)"}",
                        success: true);
                }
                else if (previousSuccess is not null && previousAllowed != allowed)
                {
                    var direction = allowed ? "ALLOW (was BLOCK)" : "BLOCK (was ALLOW)";
                    await audit.RecordAsync(
                        kind: "tx-control",
                        bearer: "remote",
                        reason: $"dev-time kill-switch {direction}: {reason ?? "(no reason)"}",
                        success: true);
                }
            }
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

    private bool IsStale(DateTimeOffset successAt)
    {
        var now = timeProvider.GetUtcNow();
        return now - successAt > TimeSpan.FromSeconds(StaleSeconds);
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
