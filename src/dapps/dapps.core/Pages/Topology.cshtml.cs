using dapps.core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace dapps.core.Pages;

/// <summary>
/// Topology page - the "who can I talk to?" surface. Tabs (URL ?tab=)
/// over the five collections that together describe the node's view of
/// its corner of the network: manually-configured neighbours, the
/// beacon channels we listen on, peers we've heard via beacons,
/// peers we've probed (B6.1), and peers we're polling for queued
/// mail (F3b). Data is rendered client-side from the snapshot the
/// layout already polls; this page model just selects the active tab.
/// </summary>
public sealed class TopologyModel(IOptionsMonitor<SystemOptions> options) : PageModel
{
    public SystemOptions Options { get; private set; } = new();
    public string Tab { get; private set; } = "neighbours";

    public void OnGet(string? tab)
    {
        Options = options.CurrentValue;
        Tab = (tab ?? "").ToLowerInvariant() switch
        {
            "channels" => "channels",
            "peers"    => "peers",
            "probes"   => "probes",
            "polls"    => "polls",
            _          => "neighbours",
        };
    }
}
