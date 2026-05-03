using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Plan C3 - operator-facing aggregate observability surface,
/// distinct from the dashboard's <c>/Events/*</c> endpoints
/// (which are tightly coupled to the dashboard's UI shape and
/// poll cadence).
///
/// <list type="bullet">
/// <item><c>GET /Operational</c> - top-level snapshot: liveness, all
///   counters, queue / peer / channel counts, trailing-hour airtime,
///   last-20 recent events. Same JSON shape as the periodic MQTT
///   heartbeat publish on <c>dapps/metrics/heartbeat</c>.</item>
/// <item><c>GET /Operational/recent</c> - the full last-100 ring as
///   JSON for ad-hoc scrapers / curl-grep workflows. Smaller body
///   than the full snapshot when you just want the event tail.</item>
/// </list>
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class OperationalController(
    OperationalMetrics metrics,
    OperationalSnapshotBuilder snapshotBuilder) : ControllerBase
{
    [HttpGet]
    public Task<OperationalSnapshot> Get(CancellationToken ct) => snapshotBuilder.BuildAsync(ct);

    [HttpGet("recent")]
    public IReadOnlyList<OperationalMetrics.OperationalEvent> GetRecent()
        => metrics.Take().RecentEvents;
}
