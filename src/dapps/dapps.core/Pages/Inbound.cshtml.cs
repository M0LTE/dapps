using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace dapps.core.Pages;

/// <summary>
/// Legacy redirect. The live arrivals feed now lives on
/// <c>/Messages?tab=live</c> alongside the outbound, inbox, and
/// dropped views. Kept so old bookmarks resolve.
/// </summary>
public sealed class InboundModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Messages", new { tab = "live" });
}
