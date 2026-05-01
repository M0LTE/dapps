using System.Security.Claims;
using dapps.core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace dapps.core.Pages;

/// <summary>
/// First-time admin password setup. The middleware redirects every
/// non-AppApi request here when no admin password is configured;
/// once the operator submits the form, the password lands in the
/// systemoptions table and the cookie scheme takes over via /Login.
///
/// If a password is already configured, GET /Setup bounces to /Login —
/// rotation lives on /Config and shouldn't accidentally be reachable
/// here. POST /Setup also no-ops with a redirect so an attacker can't
/// silently overwrite an existing password by replaying a stale form.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class SetupModel(AdminPasswordStore store) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await store.IsConfiguredAsync()) return LocalRedirect("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await store.IsConfiguredAsync())
        {
            // Race: another request set the password between our load
            // and submit. Just send the operator to /Login; their form
            // input is already redundant.
            return LocalRedirect("/Login");
        }

        if (string.IsNullOrEmpty(Password) || Password.Length < 8)
        {
            Error = "Password must be at least 8 characters.";
            return Page();
        }
        if (Password != Confirm)
        {
            Error = "Passwords don't match.";
            return Page();
        }

        await store.SetAsync(Password);

        // Sign the operator in immediately so they don't need to enter
        // the password they just set on the very next page.
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

        return LocalRedirect("/");
    }
}
