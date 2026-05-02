using dapps.core.Services;
using Microsoft.AspNetCore.Mvc;

namespace dapps.core.Controllers;

/// <summary>
/// Plan C3 — operator-facing aggregate observability surface,
/// distinct from the dashboard's <c>/Events/*</c> endpoints
/// (which are tightly coupled to the dashboard's UI shape and
/// poll cadence).
///
/// PR-A surfaces the recent-decisions ring as a stable JSON path
/// for external scrapers and ad-hoc curl. PR-B will add a top-
/// level snapshot under <c>/Operational</c> that aggregates
/// counters + neighbours + airtime + queue counts in one shot.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class OperationalController(OperationalMetrics metrics) : ControllerBase
{
    [HttpGet("recent")]
    public IReadOnlyList<OperationalMetrics.OperationalEvent> GetRecent()
        => metrics.Take().RecentEvents;
}
