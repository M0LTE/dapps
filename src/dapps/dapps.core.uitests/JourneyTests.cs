using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// End-to-end journey: drive the first-time-setup form, log in, and
/// land on the dashboard + protected pages. Screenshots every step so
/// future UI work has a baseline to compare against.
///
/// Uses <see cref="IClassFixture{TFixture}"/> for the WebApp so this
/// class gets its own fresh dapps subprocess (pre-configured admin
/// password, logged-in cookie) without affecting the empty-DB
/// assertions in <see cref="SmokeTests"/>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class JourneyTests(PlaywrightFixture pw) : IAsyncLifetime
{
    // Per-test fresh dapps subprocess: each journey test sets up the
    // admin password as part of its flow, and we don't want test
    // order to matter. Bool-checked so re-init doesn't re-spawn.
    private readonly WebAppFixture _app = new();
    private const string AdminPassword = "ui-test-password";

    private static readonly string ScreenshotDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "screenshots");

    public async ValueTask InitializeAsync() => await _app.InitializeAsync();
    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task SetupAndExploreProtectedPages_AllRender()
    {
        await using var ctx = await pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await ctx.NewPageAsync();

        // 1. Land on /Setup, fill the password step, submit. The
        // handler sets the cookie and redirects back to /Setup,
        // which then renders the bearer step (callsign + node).
        await page.GotoAsync(_app.BaseUrl);
        page.Url.Should().EndWith("/Setup");
        await page.FillAsync("input[name='password']", AdminPassword);
        await page.FillAsync("input[name='confirm']", AdminPassword);
        await page.ClickAsync("button[type='submit']");

        // Bearer step: wait for the callsign input to appear, fill
        // it in, submit. Once the callsign is set, the wizard's
        // OnGet routes us to "/" on next nav.
        await page.WaitForSelectorAsync("input[name='callsign']",
            new PageWaitForSelectorOptions { Timeout = 10_000 });
        await CaptureAsync(page, "setup-bearer");
        await page.FillAsync("input[name='callsign']", "TEST-7");
        await page.ClickAsync("#bearerForm button[type='submit']");
        await page.WaitForURLAsync(url => !url.Contains("/Setup", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 10_000 });

        page.Url.TrimEnd('/').Should().Be(_app.BaseUrl.TrimEnd('/'),
            "completing the wizard should drop us on the dashboard root");
        await CaptureAsync(page, "dashboard");

        // 2. /Messages?tab=live - SSE-driven live tail (replaces the
        // legacy /Inbound page). The cookie carries through, so we
        // should land directly on the page (no bounce to /Login).
        // DOMContentLoaded rather than NetworkIdle: the EventSource
        // opens a long-lived connection that NetworkIdle would wait
        // on forever.
        await page.GotoAsync($"{_app.BaseUrl}/Messages?tab=live", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        page.Url.Should().Contain("/Messages");
        (await page.Locator("#filter-app").CountAsync())
            .Should().Be(1, "the live-arrivals filter inputs should be present");
        await CaptureAsync(page, "messages-live");

        // /Inbound legacy URL must redirect to /Messages?tab=live so
        // old bookmarks resolve.
        await page.GotoAsync($"{_app.BaseUrl}/Inbound", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        page.Url.Should().Contain("/Messages");

        // 3. /Compose - manual ihave form (renamed from /IHave).
        await page.GotoAsync($"{_app.BaseUrl}/Compose");
        page.Url.Should().Contain("/Compose");
        (await page.Locator("form").CountAsync()).Should().BeGreaterThan(0);
        await CaptureAsync(page, "compose");

        // 4. Drive the live-arrivals filter inputs - proves the
        // journey harness can do interactive flows (typing, observing
        // filter-summary text update). Empty page so no rows to hide,
        // but the summary text should swap to a "showing X of Y rows"
        // form once any filter has content. Right now there are zero
        // rows so the summary's count math will read "showing 0 of 0
        // rows" - the *transition* off "showing all rows" is what
        // we're proving the JS does.
        await page.GotoAsync($"{_app.BaseUrl}/Messages?tab=live", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.FillAsync("#filter-app", "mail");
        var summary = await page.Locator("#filter-summary").InnerTextAsync();
        summary.Should().NotBe("showing all rows",
            "typing a filter value should switch the summary off the 'all rows' label");
        await CaptureAsync(page, "messages-live-filtered");
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
