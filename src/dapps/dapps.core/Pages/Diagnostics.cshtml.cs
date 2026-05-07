using dapps.core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Diagnostics page - the transmissions audit log plus operator
/// quick-actions (probe sweep / poll sweep / solicit). The audit table
/// is the bulk of the page; on-demand actions live in the header so
/// they are reachable from one click rather than buried in the per-row
/// menus on the Topology probed/polled tabs.
/// </summary>
public sealed class DiagnosticsModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();

    public void OnGet()
    {
        Options = options.CurrentValue;
    }
}
