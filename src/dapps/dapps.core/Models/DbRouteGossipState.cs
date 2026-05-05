using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Per-(local, remote) timestamp tracking the last time the local
/// daemon pulled the <c>routes</c> command from the remote. The
/// piggyback gate consults this row before adding a routes exchange
/// to an otherwise-unrelated session: if the previous pull is younger
/// than <c>SystemOptions.RouteGossipStalenessHours</c>, the gate
/// declines and the session proceeds without the gossip step.
///
/// Persisted (rather than in-memory) so a restart-happy node doesn't
/// thrash neighbours' airtime by re-pulling immediately. Gossip-only;
/// not on the message path, so a missing or out-of-date row has no
/// effect on actual delivery.
/// </summary>
[Table("routegossipstate")]
public sealed class DbRouteGossipState
{
    /// <summary>Composite key <c>{LocalCallsign}|{RemoteCallsign}</c>.</summary>
    [PrimaryKey, NotNull]
    public string Key { get; set; } = "";

    public string LocalCallsign { get; set; } = "";
    public string RemoteCallsign { get; set; } = "";

    public DateTime LastPulledAt { get; set; } = DateTime.MinValue;

    public static string MakeKey(string localCallsign, string remoteCallsign)
        => $"{localCallsign}|{remoteCallsign}";
}
