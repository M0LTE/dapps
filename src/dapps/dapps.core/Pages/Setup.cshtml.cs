using System.Security.Claims;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace dapps.core.Pages;

/// <summary>
/// First-time setup wizard. Two steps the daemon walks the operator
/// through:
///
/// <list type="number">
/// <item><description><b>Password</b> - admin password for the dashboard.
/// First step on a fresh install; the cookie scheme picks up from
/// here on subsequent visits.</description></item>
/// <item><description><b>Bearer</b> - callsign + node host + AGW or
/// RHPv2 choice (with a "Detect" button that probes localhost via
/// /Config/detect-bearer). Saved into <see cref="SystemOptions"/>;
/// the inbound bearer service hot-reloads via OnChange and binds the
/// callsign within ~5s.</description></item>
/// </list>
///
/// State machine on GET:
/// <list type="bullet">
/// <item><description>No admin password configured → render password step.</description></item>
/// <item><description>Admin password set, callsign is the placeholder → render bearer step (assumes the cookie is already valid; the password POST signs the operator in immediately).</description></item>
/// <item><description>Both configured → bounce to <c>/</c>. Existing operators never see this page.</description></item>
/// </list>
///
/// AdminAuthMiddleware allowlists <c>/Setup</c> unconditionally, the
/// same way <c>/Login</c> is allowlisted - the chicken-and-egg case
/// (browse to a fresh node, no password, no cookie) needs the page
/// reachable without auth, and once a password is set the operator's
/// just-acquired cookie carries them through the bearer step.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class SetupModel(
    AdminPasswordStore store,
    SystemOptionsStore optionsStore) : PageModel
{
    public enum SetupStep { Password, Bearer }

    public SetupStep Step { get; private set; }

    // Step 1 - password.
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";

    // Step 2 - bearer.
    [BindProperty] public string Callsign { get; set; } = "";
    [BindProperty] public string NodeHost { get; set; } = "localhost";
    [BindProperty] public string NodeBearer { get; set; } = "agw";
    [BindProperty] public int AgwPort { get; set; } = 8000;
    [BindProperty] public int RhpPort { get; set; } = 9000;
    [BindProperty] public string RhpUser { get; set; } = "";
    [BindProperty] public string RhpPass { get; set; } = "";

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await store.IsConfiguredAsync())
        {
            Step = SetupStep.Password;
            return Page();
        }

        var opts = optionsStore.CurrentValue;
        if (IsCallsignPlaceholder(opts.Callsign))
        {
            // Operator set the password but hasn't done bearer yet.
            // Pre-fill the form from the persisted defaults so they
            // only type what they want to change.
            Step = SetupStep.Bearer;
            Callsign = "";
            NodeHost = string.IsNullOrWhiteSpace(opts.NodeHost) ? "localhost" : opts.NodeHost;
            NodeBearer = string.Equals(opts.NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase) ? "rhpv2" : "agw";
            AgwPort = opts.AgwPort > 0 ? opts.AgwPort : 8000;
            RhpPort = opts.RhpPort > 0 ? opts.RhpPort : 9000;
            RhpUser = opts.RhpUser ?? "";
            RhpPass = opts.RhpPass ?? "";
            return Page();
        }

        // Both done - the wizard has nothing left to ask.
        return LocalRedirect("/");
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (await store.IsConfiguredAsync())
        {
            // Race: another request set the password between our load
            // and submit. Fall through to /Setup so the wizard re-
            // evaluates state (probably onto the bearer step).
            return LocalRedirect("/Setup");
        }

        if (string.IsNullOrEmpty(Password) || Password.Length < 8)
        {
            Step = SetupStep.Password;
            Error = "Password must be at least 8 characters.";
            return Page();
        }
        if (Password != Confirm)
        {
            Step = SetupStep.Password;
            Error = "Passwords don't match.";
            return Page();
        }

        await store.SetAsync(Password);

        // Sign the operator in immediately so they don't need to
        // enter the password they just set on the very next page.
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "sysop") },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
            });

        // Re-enter /Setup; OnGetAsync will route to the bearer step.
        return LocalRedirect("/Setup");
    }

    public async Task<IActionResult> OnPostBearerAsync()
    {
        // Defensive: the bearer step requires admin auth (the password
        // step's sign-in covers the normal path; a hand-crafted POST
        // without a cookie shouldn't be able to set the callsign).
        if (User.Identity is not { IsAuthenticated: true })
        {
            return LocalRedirect("/Login");
        }

        Step = SetupStep.Bearer;

        var trimmedCall = (Callsign ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmedCall))
        {
            Error = "Callsign is required.";
            return Page();
        }
        if (IsCallsignPlaceholder(trimmedCall))
        {
            Error = "Pick a real callsign - the placeholder won't go on the air.";
            return Page();
        }

        var opts = optionsStore.CurrentValue;
        opts.Callsign = trimmedCall;
        opts.NodeHost = string.IsNullOrWhiteSpace(NodeHost) ? "localhost" : NodeHost.Trim();
        opts.NodeBearer = string.Equals(NodeBearer, "rhpv2", StringComparison.OrdinalIgnoreCase) ? "rhpv2" : "agw";
        if (AgwPort is > 0 and <= 65535) opts.AgwPort = AgwPort;
        if (RhpPort is > 0 and <= 65535) opts.RhpPort = RhpPort;
        opts.RhpUser = RhpUser ?? "";
        opts.RhpPass = RhpPass ?? "";

        await optionsStore.SaveAsync(opts);

        return LocalRedirect("/");
    }

    private static bool IsCallsignPlaceholder(string callsign) =>
        string.IsNullOrWhiteSpace(callsign)
        || string.Equals(callsign, DbStartup.PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
}
