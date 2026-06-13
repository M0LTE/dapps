using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace dapps.core.Updater;

/// <summary>Real GitHub Releases / HTTP implementation of
/// <see cref="IUpdaterDownloader"/>. Reuses the same User-Agent
/// convention as <see cref="Services.UpdateChecker"/> so the API
/// access logs identify dapps requests cleanly.</summary>
public sealed class RealUpdaterDownloader(
    IHttpClientFactory httpClientFactory,
    ILogger<RealUpdaterDownloader> logger) : IUpdaterDownloader
{
    private const string ReleasesUrl = "https://api.github.com/repos/packet-net/dapps/releases/latest";

    public async Task<LatestReleaseInfo?> GetLatestAsync(string rid, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("github-update");
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"dapps-updater/{rid}");
        client.Timeout = TimeSpan.FromSeconds(30);

        var release = await client.GetFromJsonAsync<GithubRelease>(ReleasesUrl, ct);
        if (release is null || string.IsNullOrEmpty(release.tag_name)) return null;
        if (release.assets is null || release.assets.Length == 0) return null;

        var assetName = $"dapps-{rid}";
        var asset = release.assets.FirstOrDefault(a =>
            string.Equals(a.name, assetName, StringComparison.Ordinal)
            // CI also publishes Windows binaries with a `.exe` suffix;
            // be tolerant for the `win-x64` case.
            || string.Equals(a.name, assetName + ".exe", StringComparison.Ordinal));
        if (asset is null || string.IsNullOrEmpty(asset.browser_download_url))
        {
            logger.LogWarning("Release {0} has no asset matching {1}", release.tag_name, assetName);
            return null;
        }

        return new LatestReleaseInfo(release.tag_name, asset.browser_download_url, asset.size);
    }

    public async Task DownloadToAsync(string url, string destPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var client = httpClientFactory.CreateClient("github-download");
        client.Timeout = TimeSpan.FromMinutes(10);   // a 100MB binary on a slow link

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Stream straight to disk so we don't buffer 100MB in memory.
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dest, ct);
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? tag_name { get; set; }
        [JsonPropertyName("assets")] public Asset[]? assets { get; set; }
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string? name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? browser_download_url { get; set; }
        [JsonPropertyName("size")] public long? size { get; set; }
    }
}
