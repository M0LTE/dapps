using SQLite;

namespace dapps.core.Models;

/// <summary>
/// A route this node has *learned* by observing inbound traffic - as
/// opposed to <see cref="DbNeighbour"/> (manual operator config) and
/// <see cref="DbRouteHint"/> (manual operator override). Populated by
/// <see cref="dapps.core.Routing.PassiveLearningAlgorithm"/>: every
/// inbound message whose F1 originator is reachable via the link
/// source teaches us the reverse direction. <c>To reach the
/// originator, send via the neighbour that handed me this message.</c>
///
/// One row per destination base callsign - newer observations
/// overwrite older ones. Future work could keep a candidate list per
/// destination with weighting, but a single best-effort entry is
/// enough for the v0.x phase.
/// </summary>
[Table("learnedroutes")]
public class DbLearnedRoute
{
    /// <summary>Base callsign (no SSID) - keyed this way so messages
    /// to <c>chat@G0FOO-1</c> and <c>mail@G0FOO-9</c> share the same
    /// learned route. SSID-specific routing is rare in practice and
    /// handled by manual neighbours when needed.</summary>
    [PrimaryKey]
    public string DestinationBaseCallsign { get; set; } = "";

    /// <summary>Full callsign with SSID of the next-hop neighbour -
    /// matches a <see cref="DbNeighbour.Callsign"/> row so the
    /// algorithm can resolve the bearer hint at use-time.</summary>
    public string NextHopCallsign { get; set; } = "";

    /// <summary>UTC timestamp of the most recent inbound message that
    /// confirmed this route. Updated on every <c>ObserveInboundAsync</c>
    /// matching this destination - a quiet route ages out implicitly
    /// when nothing's been heard for a while (sweep policy TBD).</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful forward
    /// using this route. Used by the dashboard / debug tooling to
    /// distinguish "route was learned" from "route is actually
    /// working."</summary>
    public DateTime LastUsedAt { get; set; }

    /// <summary>Count of consecutive forward failures since the last
    /// success. Once this hits the algorithm's invalidation threshold
    /// (default 3), the row is deleted and the next forward attempt
    /// falls back to whatever's available (other static sources, or
    /// in PR-C, a bounded flood).</summary>
    public int ConsecutiveFailures { get; set; }
}
