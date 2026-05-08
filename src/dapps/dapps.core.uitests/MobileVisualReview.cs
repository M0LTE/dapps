using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// One-off visual capture at mobile + desktop viewports. Not part of
/// the regression suite - drives every operator-facing page and dumps
/// screenshots to <c>tests/screenshots/{mobile,desktop}/</c> so a
/// frontend pass can eyeball what the operator on each form factor
/// actually sees. Both viewports captured so any mobile-targeted CSS
/// change can be diffed against the desktop baseline.
///
/// Skipped by default; set <c>DAPPS_RUN_MOBILE_REVIEW=1</c> to enable.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class MobileVisualReview(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    private static readonly string MobileDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "screenshots", "mobile");
    private static readonly string DesktopDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "screenshots", "desktop");

    [Fact]
    public async Task Capture_All_Pages_At_iPhone_And_Desktop_Viewports()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DAPPS_RUN_MOBILE_REVIEW"), "1", StringComparison.Ordinal))
        {
            return; // opt-in
        }
        await CaptureAsync(MobileDir, new BrowserNewContextOptions
        {
            // iPhone 13 / 14 logical viewport.
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            DeviceScaleFactor = 3,
            IsMobile = true,
            HasTouch = true,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/120.0 Mobile/15E148 Safari/604.1",
        });
        await CaptureAsync(DesktopDir, new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
    }

    private async Task CaptureAsync(string outDir, BrowserNewContextOptions options)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app, options);
        var page = await ctx.NewPageAsync();

        Directory.CreateDirectory(outDir);

        var pages = new (string Url, string Name)[]
        {
            ("/",                          "01-overview"),
            ("/Messages?tab=outbound",     "02-messages-outbound"),
            ("/Messages?tab=inbox",        "03-messages-inbox"),
            ("/Messages?tab=dropped",      "04-messages-dropped"),
            ("/Messages?tab=live",         "05-messages-live"),
            ("/Topology?tab=neighbours",   "06-topology-neighbours"),
            ("/Topology?tab=channels",     "07-topology-channels"),
            ("/Topology?tab=peers",        "08-topology-peers"),
            ("/Topology?tab=probes",       "09-topology-probes"),
            ("/Topology?tab=polls",        "10-topology-polls"),
            ("/Compose",                   "11-compose"),
            ("/Streams",                   "12-streams"),
            ("/Diagnostics",               "13-diagnostics"),
            ("/Settings",                  "14-settings"),
        };

        foreach (var (url, name) in pages)
        {
            await page.GotoAsync($"{app.BaseUrl}{url}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            // Let the layout's snapshot poll land + listeners apply.
            await page.WaitForTimeoutAsync(1000);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(outDir, $"{name}.png"),
                FullPage = true,
            });
        }
    }
}
