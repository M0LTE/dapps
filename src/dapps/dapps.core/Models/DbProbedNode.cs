using SQLite;

namespace dapps.core.Models;

/// <summary>
/// Plan B6.1 — connected-mode probe-and-map. One row per known
/// callsign that the prober has either tried, or that an operator
/// has flagged opt-out. The schedule runs L2 connects to each
/// row's <see cref="Callsign"/> on the chosen <see cref="LastBpqPort"/>
/// and confirms a <c>DAPPSv1&gt;</c> prompt comes back; the result
/// updates this row.
///
/// Distinct from <see cref="DbDiscoveredPeer"/> (which is "I heard a
/// beacon on this channel"): this row says "I successfully reached
/// the DAPPS layer at this callsign over a connected-mode L2 path."
/// </summary>
[Table("probednodes")]
public sealed class DbProbedNode
{
    /// <summary>Full callsign with SSID (e.g. <c>M0LTE-9</c>) — same
    /// shape as <see cref="DbNeighbour.Callsign"/>. Case-normalised
    /// to upper at write time.</summary>
    [PrimaryKey]
    public string Callsign { get; set; } = "";

    /// <summary>BPQ port byte (0-indexed) used on the most recent
    /// probe attempt. Null if we've never attempted, or if the row
    /// was operator-created opt-out without a probe.</summary>
    public int? LastBpqPort { get; set; }

    /// <summary>UTC timestamp of the most recent probe attempt, success
    /// or otherwise. Null = never attempted.</summary>
    public DateTime? LastProbedAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful probe
    /// (DAPPSv1&gt; prompt observed). Null = never reached.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Last error string, populated on a failed probe.
    /// Empty when the most recent attempt succeeded.</summary>
    public string LastError { get; set; } = "";

    /// <summary>Count of consecutive failed probes. Resets to 0 on
    /// any successful probe. The dashboard surfaces this so a sysop
    /// can spot peers going dark.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Cumulative count of successful probes — purely for
    /// the dashboard's "this peer's been reachable N times" stat.</summary>
    public int SuccessCount { get; set; }

    /// <summary>Operator opt-out: skip this peer in the scheduled
    /// sweep. On-demand probes via the REST endpoint still go through
    /// — operators sometimes want to test an opted-out peer manually.</summary>
    public bool OptOut { get; set; }
}
