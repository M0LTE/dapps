using System.Text.Json.Serialization;

namespace dapps.core.Updater;

/// <summary>
/// State machine for the <c>--apply-update</c> run, persisted as JSON
/// to <see cref="UpdaterPaths.StatusPath"/>. The dashboard polls this
/// for live progress; the file is the only handle a sysop has into
/// what the privileged updater is doing. Plan C5.2.
///
/// Serialised as the enum name (e.g. <c>"Success"</c>) - the
/// <see cref="JsonStringEnumConverter"/> attribute below pins this
/// because the dashboard JS pattern-matches on the name to render
/// the phase pill / colour. Default System.Text.Json behaviour is
/// integer encoding, which silently broke the dashboard when the
/// status file ended up holding <c>"phase": 6</c> instead of
/// <c>"phase": "Success"</c> - no comparison matched and the pill
/// stayed at <c>-</c> through a successful update run on
/// gb7rdg-node (caught during the v0.18.0 deploy test).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpdatePhase
{
    /// <summary>No update has run since installation, or the file was
    /// reset. The dashboard treats this as "nothing happening".</summary>
    Idle,
    /// <summary>Polling GitHub for the latest release tag.</summary>
    Checking,
    /// <summary>Fetching the new binary asset.</summary>
    Downloading,
    /// <summary>Atomically swapping <c>dapps</c> with the new binary
    /// (and parking the old one as <c>dapps.previous</c>).</summary>
    Swapping,
    /// <summary>Restarting the dapps systemd unit.</summary>
    Restarting,
    /// <summary>Watching the new dapps for the health-check window
    /// (60 s) before declaring the update complete.</summary>
    Verifying,
    /// <summary>The new binary booted and stayed up - committed.</summary>
    Success,
    /// <summary>The new binary failed health-check; <c>dapps.previous</c>
    /// has been restored and the unit restarted on the old binary.</summary>
    RolledBack,
    /// <summary>The update couldn't run at all (e.g. GitHub unreachable,
    /// asset for our RID missing). No swap happened.</summary>
    Failed,
}

/// <summary>One JSON document persisted as the updater progresses.</summary>
public sealed record UpdateStatus(
    [property: JsonPropertyName("phase")] UpdatePhase Phase,
    [property: JsonPropertyName("from_version")] string FromVersion,
    [property: JsonPropertyName("to_version")] string? ToVersion,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("error")] string? Error);
