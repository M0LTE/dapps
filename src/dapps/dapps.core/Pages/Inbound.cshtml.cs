using dapps.core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Plan D2 - live SSE-driven inbound-message view. Razor page is a
/// thin shell; the work happens in browser JS subscribing to the
/// existing <c>/Events/inbound</c> stream. Distinct from the
/// dashboard's "Recent activity" panel because the feed scope is
/// browser-session-lifetime instead of bounded ring-history.
/// </summary>
public sealed class InboundModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();

    public void OnGet()
    {
        Options = options.CurrentValue;
    }
}
