using System.ComponentModel;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan G (MCP) bootstrap - first DAPPS tool exposed to MCP clients.
/// Wraps <see cref="OperationalSnapshotBuilder"/> so an LLM has the
/// same picture of the node a sysop gets from
/// <c>GET /Operational</c>: counters, queue counts, peers, channels,
/// trailing-hour airtime, last-20 events. One tool to start; the
/// fuller PR-A landed on top of this bootstrap will add fine-grained
/// reads (per-callsign probe state, per-message journey, etc.).
///
/// Constructor injection works because we register tools via
/// <c>AddMcpServer().WithTools&lt;DappsHealthTools&gt;()</c> in
/// <c>Program.cs</c>; the MCP runtime resolves the type from the
/// service provider per call.
/// </summary>
[McpServerToolType]
public sealed class DappsHealthTools(
    OperationalSnapshotBuilder snapshotBuilder,
    OperationalMetrics metrics,
    Database database)
{
    [McpServerTool(Name = "get_operational_snapshot")]
    [Description(
        "Returns the canonical 'what's going on with this DAPPS node?' snapshot - same shape as GET /Operational. " +
        "Includes liveness (node reachable, MQTT broker up, callsign configured), every counter from the in-memory " +
        "metrics ring (forwards, probes, polls, route learnings, peer age-outs, budget refusals), queue/peer/channel/" +
        "neighbour counts, trailing-hour airtime usage, and the last 20 decision events. Use this as the first tool " +
        "to call when answering 'how is this node doing right now'.")]
    public async Task<OperationalSnapshot> GetOperationalSnapshot(CancellationToken ct)
        => await snapshotBuilder.BuildAsync(ct);

    [McpServerTool(Name = "get_recent_events")]
    [Description(
        "Returns the in-memory decision-events ring (last 100). Each entry has a kind " +
        "(e.g. forward.ok, forward.fail, probe.ok, probe.fail, poll.ok, poll.fail, route.learned, peer.aged, " +
        "budget.refused, ttl.expired, route.none, agw.reconnect, inbound.connect, hash.mismatch) and a free-form " +
        "summary. Pass `kindPrefix` (e.g. 'forward.', 'probe.') to filter - case-insensitive prefix match. " +
        "For events older than ~100 entries, see `journalctl -u dapps -g 'event '` on the host (every event is " +
        "ALSO emitted as a structured log line for retrospective greps).")]
    public IReadOnlyList<OperationalMetrics.OperationalEvent> GetRecentEvents(
        [Description("Optional case-insensitive prefix filter on the event kind (e.g. 'forward.', 'probe.fail').")]
        string? kindPrefix = null)
    {
        var all = metrics.Take().RecentEvents;
        if (string.IsNullOrEmpty(kindPrefix)) return all;
        return all
            .Where(e => e.Kind.StartsWith(kindPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    [McpServerTool(Name = "get_system_options")]
    [Description(
        "Returns the persisted SystemOptions row - every operator-tunable knob: callsign, node host/port, " +
        "MQTT port, fragmenting threshold, probing/polling/heartbeat enable + cadences, routing algorithm, " +
        "discovery airtime budget, etc. Same shape as GET /Config (which is gated behind admin auth). Read-only.")]
    public async Task<dapps.core.Models.SystemOptions> GetSystemOptionsAsync()
        => await database.GetSystemOptions();
}
