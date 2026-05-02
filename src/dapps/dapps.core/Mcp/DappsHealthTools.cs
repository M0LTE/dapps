using System.ComponentModel;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan G (MCP) bootstrap — first DAPPS tool exposed to MCP clients.
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
public sealed class DappsHealthTools(OperationalSnapshotBuilder snapshotBuilder)
{
    [McpServerTool(Name = "get_operational_snapshot")]
    [Description(
        "Returns the canonical 'what's going on with this DAPPS node?' snapshot — same shape as GET /Operational. " +
        "Includes liveness (BPQ AGW reachable, MQTT broker up, callsign configured), every counter from the in-memory " +
        "metrics ring (forwards, probes, polls, route learnings, peer age-outs, budget refusals), queue/peer/channel/" +
        "neighbour counts, trailing-hour airtime usage, and the last 20 decision events. Use this as the first tool " +
        "to call when answering 'how is this node doing right now'.")]
    public async Task<OperationalSnapshot> GetOperationalSnapshot(CancellationToken ct)
        => await snapshotBuilder.BuildAsync(ct);
}
