using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Settings page. Renders a single SystemOptions form, grouped into
/// "Identity &amp; bearer", "App interface", "Discovery &amp; airtime",
/// "Routing &amp; probing", "Polling", "Heartbeat", "Updates", and
/// "Admin password" panels. Posts JSON to <c>/Config</c>; the daemon
/// hot-reloads everything except the MQTT and UDP listener ports.
///
/// <para>Fields whose <c>DAPPS_*</c> env var is set on the daemon's
/// environment are deployment-managed: <see cref="DbStartup"/>
/// re-applies the env value at every start, so dashboard edits would
/// be silently overridden. <see cref="EnvManaged"/> feeds the page
/// script that badges those fields and makes them read-only.</para>
/// </summary>
public sealed class SettingsModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public sealed record EnvManagedField(string Key, string Env);

    public SystemOptions Options { get; private set; } = new();

    public IReadOnlyList<EnvManagedField> EnvManaged { get; private set; } = [];

    public void OnGet()
    {
        Options = options.CurrentValue;
        EnvManaged = DbStartup.EnvManagedKeys()
            .Select(k => new EnvManagedField(k, DbStartup.EnvVarFor(k)))
            .ToArray();
    }
}
