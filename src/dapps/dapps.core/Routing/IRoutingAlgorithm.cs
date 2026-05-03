using dapps.client.Backhaul;
using dapps.core.Models;

namespace dapps.core.Routing;

/// <summary>
/// The strategy for "where should this message go, and what should
/// I learn from inbound / forward outcomes?". Plug-in seam for B5/B6
/// - see <c>docs/routing-prior-art.md</c> for the comparison of
/// candidate algorithms (AODV / Babel / NET-ROM / Reticulum /
/// MeshCore) and the rationale for the interface shape.
///
/// Hooks (in order of how often they fire):
///
/// <list type="number">
/// <item><see cref="ObserveInboundAsync"/> - every message that arrives,
///   anywhere in the chain. Algorithms learn reverse-direction routes
///   from this (passive learning) and reset failure counters when a
///   peer becomes reachable.</item>
/// <item><see cref="ResolveAsync"/> - once per pending message per
///   forwarder run, returns where to send it.</item>
/// <item><see cref="ObserveForwardOutcomeAsync"/> - once per actual
///   send, with success/failure result. Algorithms invalidate stale
///   routes on failure and confirm liveness on success.</item>
/// <item><see cref="RunAsync"/> - long-running background loop. Empty
///   for purely-reactive algorithms; populated for proactive ones
///   (Babel-style periodic announcements, B6.1 probe-and-map, etc.).
///   Cancellation token signals shutdown.</item>
/// </list>
///
/// Algorithms MUST be safe to call <see cref="ResolveAsync"/> and the
/// observe hooks concurrently - the hot path runs on the forwarder's
/// loop and the inbox's delivery callback in parallel.
/// </summary>
public interface IRoutingAlgorithm
{
    /// <summary>
    /// Decide where to forward <paramref name="message"/>. The OMM
    /// pattern-matches on the returned <see cref="RouteDecision"/>:
    /// <see cref="RouteDecision.NextHop"/> picks a backhaul and sends;
    /// <see cref="RouteDecision.Unreachable"/> leaves the message in
    /// the queue.
    /// </summary>
    Task<RouteDecision> ResolveAsync(DbMessage message, IRoutingContext ctx, CancellationToken ct);

    /// <summary>
    /// Hook for every inbound message - both messages destined for a
    /// local app and messages just transiting this node. Algorithms
    /// learn reverse routes from F1's <c>src=</c> here: the
    /// originator <paramref name="message"/>.<see cref="BackhaulMessage.Originator"/>
    /// is reachable via the neighbour <paramref name="linkSourceCallsign"/>.
    /// </summary>
    Task ObserveInboundAsync(BackhaulMessage message, string linkSourceCallsign, IRoutingContext ctx, CancellationToken ct);

    /// <summary>
    /// Hook called by the forwarder after each <see cref="IDappsBackhaul.SendAsync"/>
    /// attempt. <paramref name="result"/> tells the algorithm whether
    /// the route worked - useful for invalidating stale learned
    /// routes and bumping success counters on routes that did work.
    /// </summary>
    Task ObserveForwardOutcomeAsync(DbMessage message, BackhaulRoute attemptedRoute, BackhaulSendResult result, IRoutingContext ctx, CancellationToken ct);

    /// <summary>
    /// Optional long-running loop. Purely reactive algorithms return
    /// immediately. Proactive algorithms (Babel, NET-ROM-style, B6
    /// active discovery) emit periodic announcements / probes here.
    /// Returns when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task RunAsync(IRoutingContext ctx, CancellationToken ct);
}
