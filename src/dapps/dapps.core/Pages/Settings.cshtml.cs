using dapps.core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Settings page. Renders a single SystemOptions form, grouped into
/// "Identity &amp; bearer", "App interface", "Discovery &amp; airtime",
/// "Routing &amp; probing", "Polling", "Heartbeat", "Updates", and
/// "Admin password" panels. Posts JSON to <c>/Config</c>; the daemon
/// hot-reloads everything except the MQTT and UDP listener ports.
/// </summary>
public sealed class SettingsModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();

    public void OnGet()
    {
        Options = options.CurrentValue;
    }
}
