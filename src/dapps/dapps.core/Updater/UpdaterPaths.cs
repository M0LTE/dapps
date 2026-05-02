namespace dapps.core.Updater;

/// <summary>
/// File-system locations the updater touches. Defaults match the
/// install layout the README + scripts/dapps.service ship; tests
/// override them. Plan C5.2.
/// </summary>
public sealed record UpdaterPaths(
    /// <summary>Live binary the systemd unit ExecStart-s. Atomically
    /// replaced on update.</summary>
    string BinaryPath,
    /// <summary>Where the previous version is parked for rollback.
    /// Always lives next to <see cref="BinaryPath"/> so the swap is a
    /// rename, not a cross-volume copy.</summary>
    string PreviousBinaryPath,
    /// <summary>Stage the downloaded new binary here before the swap;
    /// keeps a partial download from clobbering a working binary.</summary>
    string NewBinaryPath,
    /// <summary>Operator's "please update" marker. Written by
    /// <c>UpdateController.Apply</c>, read + deleted by
    /// <c>--apply-update</c>. Empty/marker; presence is the signal.</summary>
    string RequestPath,
    /// <summary>Most recent updater state. JSON; written by
    /// <c>--apply-update</c>, read by the dashboard for live status.</summary>
    string StatusPath)
{
    public static UpdaterPaths Default { get; } = new(
        BinaryPath: "/opt/dapps/dapps",
        PreviousBinaryPath: "/opt/dapps/dapps.previous",
        NewBinaryPath: "/opt/dapps/dapps.new",
        RequestPath: "/var/lib/dapps/update-requested",
        StatusPath: "/var/lib/dapps/update-status");
}
