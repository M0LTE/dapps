using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST app interface — mirrors the MQTT topic structure for apps that
/// prefer plain HTTP. Same SQLite-backed queue, same ack contract.
///
/// Map (MQTT → REST):
///   `dapps/out/&lt;app&gt;/&lt;dest&gt;` → POST /AppApi/outbound
///   `dapps/in/&lt;app&gt;`           → GET  /AppApi/inbound/{app}
///   `dapps/ack/&lt;app&gt;`          → POST /AppApi/inbound/{app}/{id}/ack
/// </summary>
[ApiController]
[Route("[controller]")]
public class AppApiController(Database database) : ControllerBase
{
    /// <summary>
    /// Submit an outbound message for forwarding to a remote DAPPS instance.
    /// Equivalent to MQTT publish on `dapps/out/{App}/{DestCallsign}`.
    /// </summary>
    [HttpPost("outbound")]
    public async Task<ActionResult<OutboundResponse>> SubmitOutbound([FromBody] OutboundRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.App)) return BadRequest("App is required");
        if (string.IsNullOrWhiteSpace(request.DestCallsign)) return BadRequest("DestCallsign is required");
        if (request.Payload is null || request.Payload.Length == 0) return BadRequest("Payload is required");

        var id = await database.SubmitOutboundMessage(request.App, request.DestCallsign, request.Payload);
        return Ok(new OutboundResponse(id));
    }

    /// <summary>
    /// List currently unacknowledged inbound messages for the given app.
    /// Equivalent to subscribing to `dapps/in/{app}` — but pull-based.
    /// </summary>
    [HttpGet("inbound/{app}")]
    public async Task<ActionResult<List<InboundMessage>>> GetInbound(string app)
    {
        var pending = await database.GetUnacknowledgedLocalMessagesForApp(app);
        return Ok(pending.Select(m => new InboundMessage(m.Id, m.Payload)).ToList());
    }

    /// <summary>
    /// Acknowledge receipt of an inbound message — once ack'd, the message
    /// no longer appears in subsequent /inbound responses or MQTT replays.
    /// Equivalent to publishing the id on `dapps/ack/{app}`.
    /// </summary>
    [HttpPost("inbound/{app}/{id}/ack")]
    public async Task<IActionResult> Ack(string app, string id)
    {
        await database.MarkLocallyDelivered(id);
        return NoContent();
    }
}

public sealed record OutboundRequest(string App, string DestCallsign, byte[] Payload);
public sealed record OutboundResponse(string Id);
public sealed record InboundMessage(string Id, byte[] Payload);
