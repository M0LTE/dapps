using dapps.client.Backhaul;

namespace dapps.core.Routing;

/// <summary>
/// A routing algorithm's answer to "where should this message go?".
/// Discriminated union — pattern-match on subtypes.
///
/// Initial cases cover today's static-routing behaviour. Future
/// algorithms add cases without breaking existing implementations:
///
/// <list type="bullet">
/// <item><see cref="NextHop"/> — point-to-point delivery to a specific
///   neighbour. The default for AODV-style and distance-vector
///   algorithms; pattern matches today's <see cref="StaticRoutingAlgorithm"/>
///   output.</item>
/// <item><see cref="Unreachable"/> — the algorithm has nothing for this
///   destination right now. The OMM leaves the message in the queue;
///   later algorithms (e.g. bounded-flood fallback) may treat
///   Unreachable as a trigger to flood instead of giving up.</item>
/// </list>
///
/// Future cases (not yet implemented; sketched here so the seam
/// design is documented and PRs adding them are obviously additive):
///
/// <list type="bullet">
/// <item><c>SourceRoute(IReadOnlyList&lt;string&gt; hops)</c> — full path
///   embedded for MeshCore-flavoured source-routed delivery (option B
///   in <c>docs/routing-prior-art.md</c>). The OMM passes the path to
///   the bearer; bearers that don't support source routing fall back
///   to using the first hop as next-hop.</item>
/// <item><c>BearerDelegated</c> — the algorithm declines to make a
///   decision; the bearer handles its own routing (e.g. AGW deferring
///   to BPQ's NET/ROM table; option C in the prior-art doc).</item>
/// <item><c>FloodToNeighbours(IReadOnlyList&lt;BackhaulRoute&gt; routes,
///   int hopBudget)</c> — bounded flood. Algorithm internally tracks
///   dedup; OMM iterates and sends.</item>
/// </list>
/// </summary>
public abstract record RouteDecision
{
    /// <summary>Forward to a single next-hop neighbour.</summary>
    public sealed record NextHop(BackhaulRoute Route) : RouteDecision;

    /// <summary>No route currently known. OMM leaves message in the
    /// queue; the message will be retried on the next forwarder tick
    /// or expired by TTL.</summary>
    public sealed record Unreachable : RouteDecision;

    /// <summary>Bounded flood — send to every listed neighbour with
    /// <see cref="HopBudget"/> as the remaining hop count. The OMM
    /// iterates <see cref="Routes"/> and stamps each outbound
    /// <see cref="BackhaulMessage.FloodHopsRemaining"/> so receivers
    /// know they're handling a flood (not a regular routed message)
    /// and decrement appropriately. <see cref="HopBudget"/> of zero
    /// means "this is the last hop; deliver to local apps if applicable
    /// but do not re-flood."</summary>
    public sealed record FloodToNeighbours(IReadOnlyList<BackhaulRoute> Routes, byte HopBudget) : RouteDecision;
}
