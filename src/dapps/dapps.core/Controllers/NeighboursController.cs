using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST surface for the neighbour table - the list of remote DAPPS nodes
/// this instance is willing to forward through. A sysop maintains it by
/// hand today; the auto-discovery work in Phase B feeds the same table.
///
/// Callsign is the natural key. POST is upsert (re-POSTing changes the
/// BPQ port without erroring), DELETE is idempotent (404 only when there
/// was nothing to delete).
/// </summary>
[ApiController]
[Route("[controller]")]
public class NeighboursController(Database database) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<NeighbourModel>> List()
    {
        var rows = await database.GetNeighbours();
        return rows.Select(n => new NeighbourModel(n.Callsign, n.BpqPort, n.UdpEndpoint));
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] NeighbourModel neighbour)
    {
        if (string.IsNullOrWhiteSpace(neighbour.Callsign))
        {
            return BadRequest("Callsign is required");
        }
        await database.UpsertNeighbour(
            neighbour.Callsign.Trim().ToUpperInvariant(),
            neighbour.BpqPort,
            string.IsNullOrWhiteSpace(neighbour.UdpEndpoint) ? null : neighbour.UdpEndpoint.Trim());
        return NoContent();
    }

    [HttpDelete("{callsign}")]
    public async Task<IActionResult> Remove(string callsign)
    {
        var removed = await database.RemoveNeighbour(callsign.Trim().ToUpperInvariant());
        return removed ? NoContent() : NotFound();
    }
}

/// <summary>
/// Wire shape for /Neighbours. <see cref="UdpEndpoint"/> ("host:port") is
/// set when this neighbour is reachable over the UDP datagram backhaul;
/// null routes via BPQ/AGW. <see cref="BpqPort"/> is the AGW port byte
/// (0-indexed) when AGW-routed; null falls back to DefaultBpqPort.
/// </summary>
public sealed record NeighbourModel(string Callsign, int? BpqPort, string? UdpEndpoint = null);
