using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Polls the GitHub Releases API on a slow cadence and exposes
/// "running version" + "latest known release" so the dashboard can
/// surface "v0.X.Y available" without the operator chasing release
/// notes by hand. Plan C5.1.
///
/// Read-only: this service never *applies* updates. Triggered and
/// auto-applied updates (C5.2 / C5.3) belong in a separate companion
/// process; dapps stays unprivileged.
///
/// Failure handling: network is flaky in the wild, so a fetch failure
/// is logged at Debug and the cached snapshot stays put. We never
/// surface "I don't know what's latest" as an error in the UI - at
/// worst the dashboard says "checking…" until the first fetch lands.
/// </summary>
public sealed class UpdateChecker(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateChecker> logger) : BackgroundService
{
    private const string ReleasesUrl = "https://api.github.com/repos/packet-net/dapps/releases/latest";
    // 1 hour: GitHub's unauthenticated rate limit is 60 req/hour/IP, so
    // one poll/hour is well within budget. 6h was too long for "I just
    // shipped, do my nodes see it?" - the dashboard would tell operators
    // their freshly-pushed release didn't exist for hours. Operators
    // wanting instant feedback hit POST /Update/check or the dashboard
    // "Check now" button.
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private LatestRelease? _latest;
    public LatestRelease? Latest => _latest;

    /// <summary>
    /// Version string baked into the running binary by MSBuild's
    /// <c>&lt;Version&gt;</c>. Dev-pushed binaries get an
    /// <c>InformationalVersion</c> override of the form <c>dev-&lt;sha&gt;</c>;
    /// the dashboard treats those as "running a dev build" and doesn't
    /// claim out-of-date.
    /// </summary>
    public string Current { get; } = ResolveCurrentVersion();

    public bool IsDevBuild =>
        Current.StartsWith("dev-", StringComparison.OrdinalIgnoreCase);

    public bool UpdateAvailable
    {
        get
        {
            if (IsDevBuild || _latest is null) return false;
            return CompareSemver(_latest.Tag, Current) > 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tiny startup delay so we don't slow first-render of the dashboard
        // and so the rest of the service surface (DB, MQTT, AGW reconnect)
        // gets a moment first.
        try { await Task.Delay(StartupDelay, timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnce(stoppingToken);
            try { await Task.Delay(PollInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Manually re-poll GitHub now, regardless of the cached cadence.
    /// Used by the dashboard's "Check now" button and the
    /// <c>POST /Update/check</c> endpoint when an operator wants
    /// instant confirmation that a freshly-shipped release is visible
    /// to this node - instead of waiting up to an hour for the next
    /// scheduled poll.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct) => PollOnce(ct);

    private async Task PollOnce(CancellationToken ct)
    {
        if (!options.CurrentValue.UpdateCheckEnabled)
        {
            logger.LogDebug("Update check disabled by config");
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("github");
            // GitHub requires a User-Agent on API calls; using the
            // version makes us identifiable in their access logs without
            // leaking anything operator-specific.
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"dapps/{Current}");
            client.Timeout = TimeSpan.FromSeconds(15);

            var release = await client.GetFromJsonAsync<GithubRelease>(ReleasesUrl, ct);
            if (release is null || string.IsNullOrEmpty(release.tag_name))
            {
                logger.LogDebug("Update check: empty / unparseable response");
                return;
            }

            _latest = new LatestRelease(
                Tag: release.tag_name.TrimStart('v'),
                Name: release.name ?? release.tag_name,
                Url: release.html_url ?? "",
                PublishedAt: release.published_at,
                FetchedAt: timeProvider.GetUtcNow().UtcDateTime);
            logger.LogInformation(
                "Update check: latest is {0} (running {1}{2})",
                _latest.Tag, Current, UpdateAvailable ? " - UPDATE AVAILABLE" : "");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Update check failed");
        }
    }

    private static string ResolveCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Strip the trailing build-metadata that .NET appends for
            // some publish modes: "0.8.0+abcd1234" → "0.8.0".
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    /// <summary>Compares two semver-ish version strings (dotted decimals,
    /// optional pre-release suffix). Returns &gt; 0 if <paramref name="a"/>
    /// is newer, &lt; 0 if older, 0 if equivalent. Tolerant: a
    /// non-parseable component is treated as 0.</summary>
    public static int CompareSemver(string a, string b)
    {
        var ap = a.Split('-')[0].Split('.');
        var bp = b.Split('-')[0].Split('.');
        var n = Math.Max(ap.Length, bp.Length);
        for (var i = 0; i < n; i++)
        {
            var av = i < ap.Length && int.TryParse(ap[i], out var x) ? x : 0;
            var bv = i < bp.Length && int.TryParse(bp[i], out var y) ? y : 0;
            if (av != bv) return av - bv;
        }
        return 0;
    }

    public sealed record LatestRelease(
        string Tag,
        string Name,
        string Url,
        DateTime? PublishedAt,
        DateTime FetchedAt);

    // Wire shape for the GitHub Releases API. Lowercase fields match
    // the JSON; we parse just what we need.
    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? tag_name { get; set; }
        [JsonPropertyName("name")] public string? name { get; set; }
        [JsonPropertyName("html_url")] public string? html_url { get; set; }
        [JsonPropertyName("published_at")] public DateTime? published_at { get; set; }
    }
}
