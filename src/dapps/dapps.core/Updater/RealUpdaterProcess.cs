using System.Diagnostics;

namespace dapps.core.Updater;

/// <summary>Real systemctl + Task.Delay implementation of
/// <see cref="IUpdaterProcess"/>.</summary>
public sealed class RealUpdaterProcess : IUpdaterProcess
{
    public async Task<int> RestartServiceAsync(string serviceName, CancellationToken ct)
    {
        // `systemctl restart` returns once the unit transition is
        // scheduled — does NOT block until the unit is fully active.
        // We follow up with poll-based is-active checks during the
        // health window.
        var psi = new ProcessStartInfo("systemctl", $"restart {serviceName}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    public async Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken ct)
    {
        // `systemctl is-active <unit>` exits 0 + prints "active" when
        // running, non-zero otherwise. We rely on the exit code; the
        // textual output is just a sanity check on the parse.
        var psi = new ProcessStartInfo("systemctl", $"is-active {serviceName}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0;
    }

    public Task DelayAsync(TimeSpan duration, CancellationToken ct)
        => Task.Delay(duration, ct);
}
