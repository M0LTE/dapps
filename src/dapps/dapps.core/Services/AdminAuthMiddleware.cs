using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace dapps.core.Services;

/// <summary>
/// Gate the dashboard and admin endpoints behind the cookie auth
/// scheme. Runs after <see cref="BearerAuthMiddleware"/>, which owns
/// <c>/AppApi/*</c> with its own bearer-token model — those requests
/// pass through here untouched.
///
/// Bootstrap-friendly: when no admin password is set yet (fresh node,
/// no <c>DAPPS_ADMIN_PASSWORD</c> on first start), the dashboard is
/// open. As soon as a password is set (via <c>/Config</c>, or via the
/// env var on a future restart), enforcement turns on.
/// </summary>
public sealed class AdminAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AdminPasswordStore store)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Pass-through for paths the middleware deliberately doesn't gate:
        //   /AppApi/*  — owned by BearerAuthMiddleware
        //   /Login, /Logout  — the auth UX itself
        //   static asset paths Razor's StaticFiles middleware would serve
        if (IsPassThrough(path))
        {
            await next(ctx);
            return;
        }

        // No password configured? Dashboard is open. Operator should
        // set DAPPS_ADMIN_PASSWORD or POST to /Config before exposing
        // the node off-loopback (the README says as much).
        if (!await store.IsConfiguredAsync())
        {
            await next(ctx);
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
            || path.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Logout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
            || path == "/favicon.ico";
    }
}
