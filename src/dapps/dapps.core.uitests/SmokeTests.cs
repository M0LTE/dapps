using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Floor-level smoke: every operator-facing Razor page renders, and
/// each one writes a PNG to <c>tests/screenshots/&lt;page&gt;.png</c>
/// so future UI work can be eyeballed without a manual `dotnet run`.
/// First-use flow lands on /Setup since the WebAppFixture starts
/// with a fresh DB on every run.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SmokeTests(WebAppFixture app, PlaywrightFixture pw)
{
    private static readonly string ScreenshotDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "screenshots");

    [Fact]
    public async Task Root_FirstRun_RedirectsToSetup_AndRenders()
    {
        await using var ctx = await pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        var page = await ctx.NewPageAsync();

        var response = await page.GotoAsync(app.BaseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
        });
        response.Should().NotBeNull();

        // First-use flow: AdminAuthMiddleware bounces / → /Setup
        // because no admin password is configured yet.
        page.Url.Should().EndWith("/Setup");
        var headingCount = await page.Locator("h1, h2").CountAsync();
        headingCount.Should().BeGreaterThan(0, "the /Setup page should render at least one heading");

        await CaptureAsync(page, "setup");
    }

    [Fact]
    public async Task Login_PreSetup_RedirectsToSetup()
    {
        // /Login itself does the bounce when the admin password isn't
        // configured yet (LoginModel.OnGet returns LocalRedirect to
        // /Setup) - gives the operator a single landing page on
        // first install rather than two confusingly-similar forms.
        await using var ctx = await pw.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Login");

        page.Url.Should().EndWith("/Setup",
            "/Login bounces to /Setup until the admin password has been configured");
    }

    [Fact]
    public async Task Health_OpenAccess_ReturnsJson()
    {
        // /Health is allowlisted past AdminAuthMiddleware (designed
        // for watchdog units that don't carry a cookie). Use the
        // request API rather than a page navigation - we want the
        // raw JSON, not a rendered HTML view.
        //
        // 200 = all components healthy; 503 = at least one degraded
        // (we expect the latter - AGW points at port 0 since no real
        // BPQ is running for the UI test). Either is a successful
        // /Health response; the failure mode we're guarding against
        // is the route being missing or 500'ing.
        await using var ctx = await pw.Browser.NewContextAsync();
        var response = await ctx.APIRequest.GetAsync($"{app.BaseUrl}/Health");
        response.Status.Should().BeOneOf(200, 503);
        var body = await response.TextAsync();
        body.Should().Contain("status", "Health endpoint should expose a status field");
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        Directory.CreateDirectory(ScreenshotDir);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(ScreenshotDir, $"{name}.png"),
            FullPage = true,
        });
    }
}
