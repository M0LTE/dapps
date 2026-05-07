using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Unified message-flow surface. Tabs (URL ?tab=) for the four
/// distinct message lifecycles an operator wants to inspect:
/// <list type="bullet">
/// <item><c>outbound</c>: queued for forwarding (someone else's destination).</item>
/// <item><c>inbox</c>: queued for local delivery (an app on this node).</item>
/// <item><c>dropped</c>: TTL-expired or otherwise removed without delivery.</item>
/// <item><c>live</c>: SSE feed of arrivals as they happen.</item>
/// </list>
/// All four read from <c>/Operational?full=true</c> (cached by _Layout)
/// or directly from <c>/Events/inbound</c> (SSE) - this page model just
/// picks the active tab.
/// </summary>
public sealed class MessagesModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public string Tab { get; private set; } = "outbound";

    public void OnGet(string? tab)
    {
        Options = options.CurrentValue;
        Tab = (tab ?? "").ToLowerInvariant() switch
        {
            "inbox"  => "inbox",
            "dropped"=> "dropped",
            "live"   => "live",
            _        => "outbound",
        };
    }
}
