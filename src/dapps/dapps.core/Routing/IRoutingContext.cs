using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// Read access to the routing-relevant slice of node state, plus the
/// "what is my callsign" identity. Passed to every
/// <see cref="IRoutingAlgorithm"/> hook so the algorithm can consult
/// the things the static resolver consults today (neighbours,
/// discovered peers, route hints) without taking a direct dependency
/// on <see cref="dapps.core.Services.Database"/>.
///
/// Future algorithms will need additional surface: writing learned
/// routes (<see cref="dapps.core.Routing.PassiveLearningAlgorithm"/>
/// in PR-B), reading flood-dedup state, injecting messages via the
/// backhauls (active discovery / B6 probing). Methods get added as
/// concrete algorithms need them — kept narrow on purpose so each
/// addition is justified by a working implementation.
/// </summary>
public interface IRoutingContext
{
    /// <summary>This node's local callsign with SSID
    /// (<c>SystemOptions.Callsign</c>).</summary>
    string LocalCallsign { get; }

    /// <summary>Default BPQ port byte (0-indexed) for AGW routes when
    /// no per-neighbour override is set.</summary>
    int DefaultBpqPort { get; }

    Task<IReadOnlyList<DbNeighbour>> GetNeighboursAsync(CancellationToken ct);

    Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeersAsync(CancellationToken ct);

    /// <summary>Look up a manual route hint for the given base
    /// callsign. Falls back to the wildcard <c>*</c> hint if no exact
    /// match. Returns the next-hop neighbour (already resolved) or
    /// null if no hint applies.</summary>
    Task<DbNeighbour?> ResolveRouteHintAsync(string destinationBaseCallsign, CancellationToken ct);
}
