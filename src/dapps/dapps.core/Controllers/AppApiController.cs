using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// REST app interface - mirrors the MQTT topic structure for apps that
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
        if (request.Ttl is { } ttl && ttl <= 0) return BadRequest("Ttl must be a positive integer (seconds)");
        if (!HttpContext.IsAuthorisedForApp(request.App)) return Forbid();

        var id = await database.SubmitOutboundMessage(request.App, request.DestCallsign, request.Payload, request.Ttl);
        return Ok(new OutboundResponse(id));
    }

    /// <summary>
    /// List currently unacknowledged inbound messages for the given app.
    /// Equivalent to subscribing to `dapps/in/{app}` - but pull-based.
    /// </summary>
    [HttpGet("inbound/{app}")]
    public async Task<ActionResult<List<InboundMessage>>> GetInbound(string app)
    {
        if (!HttpContext.IsAuthorisedForApp(app)) return Forbid();
        var pending = await database.GetUnacknowledgedLocalMessagesForApp(app);
        var now = DateTime.UtcNow;
        return Ok(pending.Select(m => new InboundMessage(
            m.Id, m.SourceCallsign, m.Payload,
            TtlMath.Residual(m.Ttl, m.CreatedAt, now),
            string.IsNullOrEmpty(m.OriginatorCallsign) ? null : m.OriginatorCallsign)).ToList());
    }

    /// <summary>
    /// Acknowledge receipt of an inbound message - once ack'd, the message
    /// no longer appears in subsequent /inbound responses or MQTT replays.
    /// Equivalent to publishing the id on `dapps/ack/{app}`.
    /// </summary>
    [HttpPost("inbound/{app}/{id}/ack")]
    public async Task<IActionResult> Ack(string app, string id)
    {
        if (!HttpContext.IsAuthorisedForApp(app)) return Forbid();
        await database.MarkLocallyDelivered(id);
        return NoContent();
    }
}

/// <summary>
/// Submit-an-outbound-message request body. <see cref="Ttl"/> is
/// optional residual lifetime in seconds - propagates onto the
/// outgoing <c>ihave</c> as <c>ttl=N</c>. Null = no expiry; the
/// message stays in the queue until forwarded or manually deleted.
/// Apps that care about cleanup should set a value.
/// </summary>
public sealed record OutboundRequest(string App, string DestCallsign, byte[] Payload, int? Ttl = null);

public sealed record OutboundResponse(string Id);

/// <summary>
/// One pending inbound message. <see cref="Ttl"/> is the residual
/// lifetime in seconds at the moment of the GET (initial TTL minus
/// dwell time on this node); null when the message has no TTL.
/// <see cref="OriginatorCallsign"/> is the F1 end-to-end source - the
/// callsign that *originated* the message - or null if the upstream
/// chain didn't tell us. Distinct from <see cref="SourceCallsign"/>
/// (link source). Apps should fall back to <c>SourceCallsign</c> when
/// <c>OriginatorCallsign</c> is null.
/// </summary>
public sealed record InboundMessage(string Id, string SourceCallsign, byte[] Payload, int? Ttl = null, string? OriginatorCallsign = null);
