using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Helpers for the post-setup UI tests. <see cref="NewLoggedInContextAsync"/>
/// returns a fresh browser context with a valid admin cookie - drives
/// the /Login form once and reuses storage state would need persisting;
/// driving /Login per-test is cheap enough for the suite size.
/// </summary>
internal static class UiTestExtensions
{
    public static async Task<IBrowserContext> NewLoggedInContextAsync(
        this IBrowser browser,
        LoggedInWebAppFixture app,
        BrowserNewContextOptions? options = null)
    {
        var ctx = await browser.NewContextAsync(options ?? new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Login");

        // The post-setup app should drop us on the login form (cookie
        // not yet set). If we somehow got bounced elsewhere, fail loud.
        if (page.Url.Contains("/Login", StringComparison.OrdinalIgnoreCase))
        {
            await page.FillAsync("input[name='password']", LoggedInWebAppFixture.AdminPassword);
            await page.ClickAsync("button[type='submit']");
            await page.WaitForURLAsync(
                url => !url.Contains("/Login", StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = 10_000 });
        }

        await page.CloseAsync();
        return ctx;
    }

    /// <summary>POST a JSON body to a dapps endpoint via Playwright's
    /// APIRequestContext, using the browser context's cookies. Throws
    /// on non-2xx so tests fail loudly when an admin endpoint regresses.</summary>
    public static async Task<IAPIResponse> PostJsonAsync(
        this IBrowserContext ctx,
        string url,
        object? body)
    {
        var resp = await ctx.APIRequest.PostAsync(url, new APIRequestContextOptions
        {
            DataObject = body,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
        });
        if (!resp.Ok)
        {
            var text = await resp.TextAsync();
            throw new InvalidOperationException($"POST {url} failed: {resp.Status} {text}");
        }
        return resp;
    }
}
