namespace dapps.core.Updater;

/// <summary>
/// Side-effect surfaces the <see cref="UpdaterOrchestrator"/> uses,
/// pulled out behind interfaces so the orchestrator's state-machine
/// logic can be unit-tested without touching the real filesystem,
/// HTTP, or systemctl. Plan C5.2.
/// </summary>
public interface IUpdaterFileSystem
{
    bool Exists(string path);

    /// <summary>Atomically replace <paramref name="dest"/> with the file at
    /// <paramref name="src"/>. Returns the path of the previous content
    /// (renamed to <c>{dest}.previous</c>) so the caller can roll back.</summary>
    void SwapInPlace(string src, string dest, string previous);

    /// <summary>Restore <paramref name="previous"/> over
    /// <paramref name="dest"/>. Used by the rollback path.</summary>
    void Restore(string previous, string dest);

    /// <summary>Make <paramref name="path"/> executable
    /// (chmod 755-equivalent on Unix). No-op on Windows.</summary>
    void MarkExecutable(string path);

    /// <summary>Read the entire contents of a small file (status / marker).
    /// Returns null if the file is absent.</summary>
    string? ReadAllText(string path);

    /// <summary>Write <paramref name="contents"/> to <paramref name="path"/>,
    /// creating or replacing. Mode 0644 on Unix.</summary>
    void WriteAllText(string path, string contents);

    /// <summary>Idempotent delete — no error if the file isn't there.</summary>
    void Delete(string path);
}

public interface IUpdaterDownloader
{
    /// <summary>Fetch the latest release tag + browser_download_url for the
    /// asset matching <paramref name="rid"/> from the GitHub Releases API.
    /// Returns null when no release exists or the API is unreachable.</summary>
    Task<LatestReleaseInfo?> GetLatestAsync(string rid, CancellationToken ct);

    /// <summary>Download the asset at <paramref name="url"/> to
    /// <paramref name="destPath"/>. Throws on HTTP error / network failure.</summary>
    Task DownloadToAsync(string url, string destPath, CancellationToken ct);
}

public sealed record LatestReleaseInfo(
    string Tag,
    string AssetUrl,
    long? AssetSize);

public interface IUpdaterProcess
{
    /// <summary>Restart the named systemd service. Returns the exit code.
    /// 0 = scheduled; non-zero = systemctl rejected the request (e.g.
    /// service not found, no permissions). Does NOT wait for the
    /// service to come back up — see <see cref="IsServiceActive"/>.</summary>
    Task<int> RestartServiceAsync(string serviceName, CancellationToken ct);

    /// <summary>Probe systemd for whether the named service is currently
    /// in the <c>active (running)</c> state.</summary>
    Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken ct);

    /// <summary>Wait for <paramref name="duration"/>. Wrapped on the
    /// interface so tests can inject a fake clock instead of really
    /// sleeping for 60 seconds.</summary>
    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}
