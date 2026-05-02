using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace dapps.core.Services;

/// <summary>
/// Gate the dashboard and admin endpoints behind the cookie auth
/// scheme. Runs after <see cref="BearerAuthMiddleware"/>, which owns
/// <c>/AppApi/*</c> with its own bearer-token model — those requests
/// pass through here untouched.
///
/// Three states:
/// <list type="bullet">
/// <item>No admin password configured (fresh install) → redirect to
///   <c>/Setup</c>. The first request lands the operator on a one-shot
///   "set your password" form; once they submit it, normal cookie auth
///   kicks in.</item>
/// <item>Password configured, no valid cookie → redirect to <c>/Login</c>
///   via the cookie scheme's challenge.</item>
/// <item>Password configured, valid cookie → pass through.</item>
/// </list>
/// </summary>
public sealed class AdminAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AdminPasswordStore store)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Pass-through for paths the middleware deliberately doesn't gate:
        //   /AppApi/*       — owned by BearerAuthMiddleware
        //   /Setup          — first-use password creation; gated by
        //                     state, not auth (see PageModel)
        //   /Login, /Logout — the auth UX itself
        //   /Health         — C3 liveness; systemd watchdog units +
        //                     external uptime monitors can't log in
        //   /Operational    — C3 metrics aggregate; same posture as
        //                     /Events/health which the dashboard JS
        //                     polls without auth context anyway
        //   /mcp            — Plan G MCP endpoint; clients (Claude,
        //                     Cursor) don't have admin cookies. An
        //                     MCP-specific token model can come later.
        //   static asset paths Razor's StaticFiles middleware serves
        if (IsPassThrough(path))
        {
            await next(ctx);
            return;
        }

        // No password configured? First-use flow. Send the operator to
        // /Setup. Until they complete it, every other path bounces
        // here.
        if (!await store.IsConfiguredAsync())
        {
            ctx.Response.Redirect("/Setup");
            return;
        }

        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            await next(ctx);
            return;
        }

        // Trigger the cookie scheme's challenge → 302 to LoginPath.
        await ctx.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static bool IsPassThrough(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith("/AppApi", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Setup", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Logout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Operational", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
            || path == "/favicon.ico";
    }
}
