using System.Text.Json;
using AwesomeAssertions;
using dapps.core.Updater;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// State-machine tests for <see cref="UpdaterOrchestrator"/>. Every
/// path the privileged <c>dapps --apply-update</c> can take, exercised
/// against mocks for the filesystem / GitHub Releases / systemctl
/// surfaces. No real binary swap, no real network, no real systemd —
/// just the orchestration logic and the status-file shape the
/// dashboard depends on.
/// </summary>
public sealed class UpdaterOrchestratorTests
{
    private static UpdaterPaths TestPaths() => new(
        BinaryPath: "/test/dapps",
        PreviousBinaryPath: "/test/dapps.previous",
        NewBinaryPath: "/test/dapps.new",
        RequestPath: "/test/update-requested",
        StatusPath: "/test/update-status");

    [Fact]
    public void UpdateStatus_Phase_SerializesAsEnumName()
    {
        // The dashboard JS pattern-matches on phase NAMES ("Success",
        // "RolledBack", …). System.Text.Json's default is integer
        // encoding which silently breaks every comparison and leaves
        // the pill at —. Pin the serialised shape so a regression
        // can't re-introduce that bug. Caught in the v0.18.0 deploy on
        // gb7rdg-node where every successful apply showed "6 · 32s ago"
        // instead of "Success · 32s ago".
        var status = new UpdateStatus(
            UpdatePhase.Success,
            FromVersion: "0.18.0",
            ToVersion: "0.18.1",
            StartedAt: new DateTime(2026, 5, 2, 11, 50, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2026, 5, 2, 11, 51, 0, DateTimeKind.Utc),
            Error: null);

        var json = JsonSerializer.Serialize(status);

        json.Should().Contain("\"phase\":\"Success\"",
            "the dashboard JS compares lr.phase === 'Success' / 'RolledBack' / 'Failed' / etc");
        json.Should().NotContain("\"phase\":6");
    }

    [Fact]
    public void UpdateStatus_Phase_RoundTripsThroughString()
    {
        var json = "{\"phase\":\"RolledBack\",\"from_version\":\"0.18.0\",\"to_version\":\"0.18.1\"," +
                   "\"started_at\":\"2026-05-02T11:00:00Z\",\"updated_at\":\"2026-05-02T11:01:00Z\"," +
                   "\"error\":\"swap failed\"}";

        var status = JsonSerializer.Deserialize<UpdateStatus>(json);

        status.Should().NotBeNull();
        status!.Phase.Should().Be(UpdatePhase.RolledBack);
        status.Error.Should().Be("swap failed");
    }

    [Fact]
    public async Task ApplyUpdate_NoRequestMarker_NoOpsImmediately()
    {
        // Common case — systemd timer fired, no operator clicked
        // "Apply update" since the last run. Exits before touching the
        // network so the timer is cheap.
        var fs = new FakeFileSystem();
        var dl = new FakeDownloader();
        var proc = new FakeProcess();

        (await MakeOrchestrator("0.17.2", fs, dl, proc).ApplyUpdateAsync(CancellationToken.None))
            .Should().Be(0);
        proc.Calls.Should().BeEmpty();
        fs.Files.Should().NotContainKey("/test/update-status",
            "no work happened, no status update");
    }

    [Fact]
    public async Task ApplyUpdate_AlreadyOnLatestVersion_NoOpsToSuccess()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader { Latest = new LatestReleaseInfo("v0.17.2", "url", null) };
        var proc = new FakeProcess();

        var rc = await MakeOrchestrator("0.17.2", fs, dl, proc).ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(0);
        proc.Calls.Should().BeEmpty("nothing to swap, nothing to restart");
        fs.Files.Should().NotContainKey("/test/dapps.new");
        var status = ReadStatus(fs);
        status.Phase.Should().Be(UpdatePhase.Success);
        status.FromVersion.Should().Be("0.17.2");
        status.ToVersion.Should().Be("0.17.2");
    }

    [Fact]
    public async Task ApplyUpdate_HappyPath_DownloadsSwapsRestartsVerifies()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old-binary-bytes";
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader(fs)
        {
            Latest = new LatestReleaseInfo("v0.18.0", "https://github.com/.../dapps-linux-x64", 100),
            DownloadContent = "new-binary-bytes",
        };
        var proc = new FakeProcess { IsActive = true };

        var orch = MakeOrchestrator("0.17.2", fs, dl, proc);
        var rc = await orch.ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(0);
        // Swap: dapps.previous holds the old bytes, dapps holds the new bytes.
        fs.Files["/test/dapps.previous"].Should().Be("old-binary-bytes");
        fs.Files["/test/dapps"].Should().Be("new-binary-bytes");
        fs.Files.ContainsKey("/test/dapps.new").Should().BeFalse("staged file moves into place, not a copy");
        // Restart issued exactly once; no extra rollback restart.
        proc.Calls.Where(c => c.StartsWith("restart:")).Should().ContainSingle();
        // Status file ends in Success.
        var status = ReadStatus(fs);
        status.Phase.Should().Be(UpdatePhase.Success);
        status.ToVersion.Should().Be("0.18.0");
        // Marker cleared on success.
        fs.Files.ContainsKey("/test/update-requested").Should().BeFalse();
    }

    [Fact]
    public async Task ApplyUpdate_GithubLookupFails_FailedNoSwap()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old";
        fs.Files["/test/update-requested"] = "requested_at=...";
        var dl = new FakeDownloader(fs) { LookupException = new HttpRequestException("DNS fail") };
        var proc = new FakeProcess();

        var rc = await MakeOrchestrator("0.17.2", fs, dl, proc).ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(2);
        fs.Files["/test/dapps"].Should().Be("old", "no swap should happen on lookup failure");
        proc.Calls.Should().BeEmpty();
        var status = ReadStatus(fs);
        status.Phase.Should().Be(UpdatePhase.Failed);
        status.Error.Should().Contain("check:");
        // Marker cleared even on failure — otherwise the next timer
        // tick would just retry and fail again indefinitely.
        fs.Files.ContainsKey("/test/update-requested").Should().BeFalse();
    }

    [Fact]
    public async Task ApplyUpdate_NoAssetForRid_FailedNoSwap()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old";
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader { Latest = null };   // GetLatestAsync returned null
        var proc = new FakeProcess();

        var rc = await MakeOrchestrator("0.17.2", fs, dl, proc).ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(2);
        proc.Calls.Should().BeEmpty();
        ReadStatus(fs).Phase.Should().Be(UpdatePhase.Failed);
    }

    [Fact]
    public async Task ApplyUpdate_DownloadFails_FailedNoSwap()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old";
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader
        {
            Latest = new LatestReleaseInfo("v0.18.0", "url", null),
            DownloadException = new IOException("link reset"),
        };
        var proc = new FakeProcess();

        var rc = await MakeOrchestrator("0.17.2", fs, dl, proc).ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(2);
        fs.Files["/test/dapps"].Should().Be("old");
        // Half-downloaded artifact must not litter /opt/dapps.
        fs.Files.ContainsKey("/test/dapps.new").Should().BeFalse();
        proc.Calls.Should().BeEmpty();
        var status = ReadStatus(fs);
        status.Phase.Should().Be(UpdatePhase.Failed);
        status.Error.Should().Contain("download:");
    }

    [Fact]
    public async Task ApplyUpdate_RestartReturnsNonZero_RollsBackAndRestarts()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old";
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader(fs)
        {
            Latest = new LatestReleaseInfo("v0.18.0", "url", null),
            DownloadContent = "new",
        };
        var proc = new FakeProcess
        {
            // First restart: fails. Second restart (post-rollback): ok.
            RestartExitCodes = new Queue<int>(new[] { 1, 0 }),
            IsActive = true,
        };

        var rc = await MakeOrchestrator("0.17.2", fs, dl, proc).ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(1, "rolled-back exit code");
        fs.Files["/test/dapps"].Should().Be("old", "previous should be restored over dapps");
        fs.Files.ContainsKey("/test/dapps.previous").Should().BeFalse(
            "previous moved back into place — not duplicated");
        proc.Calls.Where(c => c.StartsWith("restart:")).Should().HaveCount(2,
            "one failed restart, one rollback restart");
        ReadStatus(fs).Phase.Should().Be(UpdatePhase.RolledBack);
    }

    [Fact]
    public async Task ApplyUpdate_NewBinaryDiesInHealthWindow_RollsBack()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old";
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader(fs)
        {
            Latest = new LatestReleaseInfo("v0.18.0", "url", null),
            DownloadContent = "new",
        };
        // Restart succeeds. is-active starts true, then flips false on
        // the second poll — the new binary crashed.
        var proc = new FakeProcess
        {
            RestartExitCodes = new Queue<int>(new[] { 0, 0 }),
            IsActiveSequence = new Queue<bool>(new[] { true, false }),
        };

        var orch = MakeOrchestrator("0.17.2", fs, dl, proc);
        // Drop both windows so the test doesn't actually wait.
        orch = new UpdaterOrchestrator(
            TestPaths(), fs, dl, proc, NullLogger<UpdaterOrchestrator>.Instance, "0.17.2")
        {
            HealthWindow = TimeSpan.FromMilliseconds(200),
            HealthPollInterval = TimeSpan.FromMilliseconds(10),
            ServiceName = "dapps.service",
        };
        var rc = await orch.ApplyUpdateAsync(CancellationToken.None);

        rc.Should().Be(1);
        fs.Files["/test/dapps"].Should().Be("old");
        ReadStatus(fs).Phase.Should().Be(UpdatePhase.RolledBack);
    }

    [Fact]
    public async Task ApplyUpdate_CompletesWhenIsActiveStaysTrueAcrossWindow()
    {
        // Fixed window so the test doesn't really wait 60 seconds.
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "old";
        fs.Files["/test/update-requested"] = "marker";
        var dl = new FakeDownloader(fs)
        {
            Latest = new LatestReleaseInfo("v0.18.0", "url", null),
            DownloadContent = "new",
        };
        var proc = new FakeProcess { IsActive = true, RestartExitCodes = new Queue<int>(new[] { 0 }) };

        var orch = new UpdaterOrchestrator(
            TestPaths(), fs, dl, proc, NullLogger<UpdaterOrchestrator>.Instance, "0.17.2")
        {
            HealthWindow = TimeSpan.FromMilliseconds(50),
            HealthPollInterval = TimeSpan.FromMilliseconds(10),
        };

        (await orch.ApplyUpdateAsync(CancellationToken.None)).Should().Be(0);
        ReadStatus(fs).Phase.Should().Be(UpdatePhase.Success);
    }

    [Fact]
    public async Task RollBack_NoPreviousBinary_FailsWithoutTouchingFs()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "current";
        var orch = MakeOrchestrator("0.17.2", fs, new FakeDownloader(), new FakeProcess());

        var rc = await orch.RollBackAsync(CancellationToken.None);

        rc.Should().Be(2);
        fs.Files["/test/dapps"].Should().Be("current");
    }

    [Fact]
    public async Task RollBack_PreviousExists_RestoresAndRestarts()
    {
        var fs = new FakeFileSystem();
        fs.Files["/test/dapps"] = "broken";
        fs.Files["/test/dapps.previous"] = "good";
        var proc = new FakeProcess { RestartExitCodes = new Queue<int>(new[] { 0 }) };
        var orch = MakeOrchestrator("0.17.2", fs, new FakeDownloader(), proc);

        var rc = await orch.RollBackAsync(CancellationToken.None);

        rc.Should().Be(0);
        fs.Files["/test/dapps"].Should().Be("good");
        fs.Files.ContainsKey("/test/dapps.previous").Should().BeFalse();
        proc.Calls.Should().Contain("restart:dapps.service");
    }

    private static UpdaterOrchestrator MakeOrchestrator(
        string currentVersion, FakeFileSystem fs, FakeDownloader dl, FakeProcess proc)
    {
        return new UpdaterOrchestrator(
            TestPaths(), fs, dl, proc, NullLogger<UpdaterOrchestrator>.Instance, currentVersion)
        {
            // Tiny windows so tests don't wait.
            HealthWindow = TimeSpan.FromMilliseconds(50),
            HealthPollInterval = TimeSpan.FromMilliseconds(5),
        };
    }

    private static UpdateStatus ReadStatus(FakeFileSystem fs)
    {
        var raw = fs.Files["/test/update-status"];
        return JsonSerializer.Deserialize<UpdateStatus>(raw)!;
    }

    private sealed class FakeFileSystem : IUpdaterFileSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public bool Exists(string path) => Files.ContainsKey(path);
        public void SwapInPlace(string src, string dest, string previous)
        {
            // Match real-fs semantics: park dest as previous, move src
            // to dest, src goes away.
            if (Files.ContainsKey(previous)) Files.Remove(previous);
            if (Files.TryGetValue(dest, out var existing))
            {
                Files[previous] = existing;
                Files.Remove(dest);
            }
            Files[dest] = Files[src];
            Files.Remove(src);
        }
        public void Restore(string previous, string dest)
        {
            if (!Files.TryGetValue(previous, out var bytes))
                throw new FileNotFoundException("missing previous", previous);
            Files[dest] = bytes;
            Files.Remove(previous);
        }
        public void MarkExecutable(string path) { /* no-op */ }
        public string? ReadAllText(string path) => Files.TryGetValue(path, out var v) ? v : null;
        public void WriteAllText(string path, string contents) => Files[path] = contents;
        public void Delete(string path) { if (Files.ContainsKey(path)) Files.Remove(path); }
    }

    private sealed class FakeDownloader(FakeFileSystem? fs = null) : IUpdaterDownloader
    {
        public LatestReleaseInfo? Latest { get; set; }
        public Exception? LookupException { get; set; }
        public string? DownloadContent { get; set; }
        public Exception? DownloadException { get; set; }

        public Task<LatestReleaseInfo?> GetLatestAsync(string rid, CancellationToken ct)
        {
            if (LookupException is not null) throw LookupException;
            return Task.FromResult(Latest);
        }
        public Task DownloadToAsync(string url, string destPath, CancellationToken ct)
        {
            if (DownloadException is not null) throw DownloadException;
            // The real downloader writes to disk; the fake plants the
            // "downloaded" bytes into the same FakeFileSystem the
            // orchestrator's swap step will read from.
            if (fs is not null)
            {
                fs.Files[destPath] = DownloadContent ?? "";
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcess : IUpdaterProcess
    {
        public List<string> Calls { get; } = new();
        public Queue<int> RestartExitCodes { get; set; } = new();
        public Queue<bool>? IsActiveSequence { get; set; }
        public bool IsActive { get; set; }

        public Task<int> RestartServiceAsync(string serviceName, CancellationToken ct)
        {
            Calls.Add($"restart:{serviceName}");
            var code = RestartExitCodes.Count > 0 ? RestartExitCodes.Dequeue() : 0;
            return Task.FromResult(code);
        }
        public Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken ct)
        {
            Calls.Add($"is-active:{serviceName}");
            if (IsActiveSequence is not null && IsActiveSequence.Count > 0)
            {
                return Task.FromResult(IsActiveSequence.Dequeue());
            }
            return Task.FromResult(IsActive);
        }
        public Task DelayAsync(TimeSpan duration, CancellationToken ct)
            => Task.CompletedTask;   // no real sleep
    }
}
