using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace dapps.core.Updater;

/// <summary>
/// Plan C5.2 — the privileged update flow, factored as a pure state
/// machine over the <see cref="IUpdaterFileSystem"/>,
/// <see cref="IUpdaterDownloader"/>, <see cref="IUpdaterProcess"/>
/// abstractions so the happy path AND every rollback path can be unit-
/// tested without root, real binaries, or systemd.
///
/// Invocation: this code runs from <c>dapps --apply-update</c>, which
/// the privileged <c>dapps-updater.service</c> ExecStart-s on a 60 s
/// timer. The dapps daemon itself stays unprivileged and just writes
/// the request marker via <c>POST /Update/apply</c>.
///
/// Health window after restart: 60 s of "is service active?" polling.
/// Failure at any step rolls back to <c>dapps.previous</c> and
/// restarts the old binary, so a botched release on a remote node
/// recovers itself.
/// </summary>
public sealed class UpdaterOrchestrator
{
    public TimeSpan HealthWindow { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>BPQ release-asset RID. Defaults to the current OS/arch
    /// pair the build system stamps; tests override.</summary>
    public string Rid { get; init; } = ResolveDefaultRid();
    public string ServiceName { get; init; } = "dapps.service";

    private readonly UpdaterPaths paths;
    private readonly IUpdaterFileSystem fs;
    private readonly IUpdaterDownloader downloader;
    private readonly IUpdaterProcess proc;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly string currentVersion;

    public UpdaterOrchestrator(
        UpdaterPaths paths,
        IUpdaterFileSystem fs,
        IUpdaterDownloader downloader,
        IUpdaterProcess proc,
        ILogger<UpdaterOrchestrator> logger,
        string currentVersion,
        TimeProvider? timeProvider = null)
    {
        this.paths = paths;
        this.fs = fs;
        this.downloader = downloader;
        this.proc = proc;
        // The CLI side-door runs the orchestrator without DI. Default
        // to system time when no TimeProvider is supplied — tests pass
        // a FakeTimeProvider to drive deadlines deterministically.
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
        this.currentVersion = currentVersion;
    }

    /// <summary>
    /// Run the <c>--apply-update</c> flow once. Returns 0 on success
    /// (including no-update-needed and no-request-pending), 1 on
    /// rolled-back, 2 on outright failure with no swap attempted.
    /// Status file is updated at every phase transition so the
    /// dashboard can follow along.
    ///
    /// First checks the request marker. The systemd timer fires this
    /// every 60 s; without the marker we exit cheaply rather than
    /// downloading a release on every tick. Operators wanting a
    /// force-update outside the dashboard can <c>touch
    /// /var/lib/dapps/update-requested</c> and re-invoke (or kick
    /// <c>dapps-updater.service</c>).
    /// </summary>
    public async Task<int> ApplyUpdateAsync(CancellationToken ct)
    {
        if (!fs.Exists(paths.RequestPath))
        {
            // Common path — timer fired, no operator request. No-op.
            return 0;
        }

        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var status = new UpdateStatus(
            UpdatePhase.Checking, currentVersion, null, startedAt, startedAt, null);
        WriteStatus(status);

        // 1. Look up latest.
        LatestReleaseInfo? latest;
        try
        {
            latest = await downloader.GetLatestAsync(Rid, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub Releases lookup failed");
            WriteStatus(status with
            {
                Phase = UpdatePhase.Failed, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime, Error = $"check: {ex.Message}",
            });
            ClearRequest();
            return 2;
        }
        if (latest is null)
        {
            logger.LogWarning("No release info returned for RID {0}", Rid);
            WriteStatus(status with
            {
                Phase = UpdatePhase.Failed, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime,
                Error = $"no release found for {Rid}",
            });
            ClearRequest();
            return 2;
        }

        var targetVersion = latest.Tag.TrimStart('v');
        status = status with { ToVersion = targetVersion, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime };

        // 2. Skip when we're already there. Belt-and-braces: the operator
        //    might click "apply" on a node that just upgraded a moment
        //    ago, or the timer fires after a release rollback.
        if (string.Equals(targetVersion, currentVersion, StringComparison.Ordinal))
        {
            logger.LogInformation("Already on v{0}; nothing to do", currentVersion);
            WriteStatus(status with { Phase = UpdatePhase.Success, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
            ClearRequest();
            return 0;
        }

        // 3. Download to a side path; never clobber the live binary
        //    until the whole download completes successfully.
        WriteStatus(status with { Phase = UpdatePhase.Downloading, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
        try
        {
            await downloader.DownloadToAsync(latest.AssetUrl, paths.NewBinaryPath, ct);
            fs.MarkExecutable(paths.NewBinaryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download failed");
            fs.Delete(paths.NewBinaryPath);
            WriteStatus(status with
            {
                Phase = UpdatePhase.Failed, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime, Error = $"download: {ex.Message}",
            });
            ClearRequest();
            return 2;
        }

        // 4. Atomic swap. After this point we're committed; failures
        //    flow through the rollback path so we always end with a
        //    runnable binary in place.
        WriteStatus(status with { Phase = UpdatePhase.Swapping, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
        try
        {
            fs.SwapInPlace(paths.NewBinaryPath, paths.BinaryPath, paths.PreviousBinaryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Atomic swap failed");
            fs.Delete(paths.NewBinaryPath);
            WriteStatus(status with
            {
                Phase = UpdatePhase.Failed, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime, Error = $"swap: {ex.Message}",
            });
            ClearRequest();
            return 2;
        }

        // 5. Restart the unit + verify it stays up. ANY failure here
        //    triggers rollback, including non-zero systemctl exit and
        //    is-active going false at any point in the health window.
        WriteStatus(status with { Phase = UpdatePhase.Restarting, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
        var restartCode = await proc.RestartServiceAsync(ServiceName, ct);
        if (restartCode != 0)
        {
            logger.LogError("systemctl restart {0} returned {1}", ServiceName, restartCode);
            return await RollBackAsync(status, $"restart returned {restartCode}", ct);
        }

        WriteStatus(status with { Phase = UpdatePhase.Verifying, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
        var deadline = timeProvider.GetUtcNow().UtcDateTime + HealthWindow;
        while (timeProvider.GetUtcNow().UtcDateTime < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await proc.DelayAsync(HealthPollInterval, ct);
            bool active;
            try
            {
                active = await proc.IsServiceActiveAsync(ServiceName, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "is-active check failed; assuming inactive");
                active = false;
            }
            if (!active)
            {
                return await RollBackAsync(status, "service went inactive during health window", ct);
            }
        }

        // 6. Stayed up the whole window — commit.
        WriteStatus(status with { Phase = UpdatePhase.Success, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
        ClearRequest();
        logger.LogInformation("Update to v{0} successful", targetVersion);
        return 0;
    }

    /// <summary>
    /// Restore <c>dapps.previous</c> over <c>dapps</c> + restart. Used
    /// both by the orchestrator's auto-rollback path AND by the
    /// <c>dapps --rollback</c> CLI command for manual operator rescue.
    /// </summary>
    public async Task<int> RollBackAsync(CancellationToken ct)
    {
        if (!fs.Exists(paths.PreviousBinaryPath))
        {
            logger.LogError("No previous binary at {0} — cannot roll back", paths.PreviousBinaryPath);
            return 2;
        }
        try
        {
            fs.Restore(paths.PreviousBinaryPath, paths.BinaryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rollback restore failed");
            return 2;
        }
        var rc = await proc.RestartServiceAsync(ServiceName, ct);
        if (rc != 0)
        {
            logger.LogError("systemctl restart {0} after rollback returned {1}", ServiceName, rc);
            return 2;
        }
        logger.LogInformation("Rollback succeeded");
        return 0;
    }

    private async Task<int> RollBackAsync(UpdateStatus status, string error, CancellationToken ct)
    {
        WriteStatus(status with { Phase = UpdatePhase.RolledBack, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime, Error = error });
        try
        {
            fs.Restore(paths.PreviousBinaryPath, paths.BinaryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rollback restore failed mid-update — node is in an inconsistent state");
            WriteStatus(status with
            {
                Phase = UpdatePhase.Failed, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime,
                Error = $"rollback restore failed: {ex.Message}",
            });
            ClearRequest();
            return 2;
        }
        var rc = await proc.RestartServiceAsync(ServiceName, ct);
        if (rc != 0)
        {
            logger.LogError("systemctl restart {0} after rollback returned {1}", ServiceName, rc);
        }
        ClearRequest();
        return 1;
    }

    private void WriteStatus(UpdateStatus status)
    {
        try
        {
            fs.WriteAllText(paths.StatusPath, JsonSerializer.Serialize(status));
        }
        catch (Exception ex)
        {
            // Status writes are best-effort; failure here is dashboard
            // visibility loss, not an update failure.
            logger.LogWarning(ex, "Failed to write update status");
        }
    }

    private void ClearRequest()
    {
        try { fs.Delete(paths.RequestPath); } catch { /* best effort */ }
    }

    /// <summary>Best-effort RID resolution. Mirrors the matrix in
    /// <c>.github/workflows/ci.yml</c> — the file names match
    /// <c>dapps-{rid}</c>.</summary>
    public static string ResolveDefaultRid()
    {
        if (OperatingSystem.IsLinux())
        {
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "linux-x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "linux-arm64",
                System.Runtime.InteropServices.Architecture.Arm => "linux-arm",
                _ => "linux-x64",
            };
        }
        if (OperatingSystem.IsMacOS()) return "osx-arm64";
        if (OperatingSystem.IsWindows()) return "win-x64";
        return "linux-x64";
    }
}
