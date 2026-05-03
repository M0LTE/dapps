using dapps.core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// /Transmissions - operator-facing view of the transmission audit log.
/// Thin Razor shell that polls /Transmissions REST + renders rows.
/// Filter inputs (kind, target callsign, only-failures) hide
/// non-matching rows live. The audit-table itself is server-paginated
/// because rows can run into the tens of thousands over the retention
/// window.
/// </summary>
public sealed class TransmissionsModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();

    public void OnGet()
    {
        Options = options.CurrentValue;
    }
}
