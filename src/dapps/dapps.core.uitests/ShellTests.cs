using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Application shell coverage: top bar, sidebar nav, theme toggle,
/// TX kill-switch. These behaviours are layout-level so they're run
/// once here rather than repeated per page.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class ShellTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Topbar_Renders_Brand_Callsign_Status_Chips()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        // Brand + callsign chip surface the operator's identity.
        (await page.Locator(".topbar .brand").CountAsync()).Should().Be(1);
        var callsign = await page.Locator(".callsign-chip").InnerTextAsync();
        callsign.Trim().Should().Be(LoggedInWebAppFixture.Callsign);

        // The four status chips are static markup, populated by the
        // /Operational poll. Only their presence is asserted here.
        foreach (var id in new[] { "ts-node", "ts-mqtt", "ts-air", "ts-version" })
        {
            (await page.Locator($"#{id}").CountAsync())
                .Should().Be(1, $"#{id} chip is part of the topbar");
        }
    }

    /// <summary>
    /// Walks every sidebar link and asserts that (a) clicking it lands
    /// on the matching URL, and (b) the .nav-link.active sentinel ends
    /// up on the link the operator just clicked - that's how the shell
    /// signals "you are here". One regression here usually means a
    /// page's <c>ViewData["Active"]</c> string drifted from the layout's.
    /// </summary>
    [Theory]
    [InlineData("Overview",    "/")]
    [InlineData("Messages",    "/Messages")]
    [InlineData("Topology",    "/Topology")]
    [InlineData("Diagnostics", "/Diagnostics")]
    [InlineData("Compose",     "/Compose")]
    [InlineData("Streams",     "/Streams")]
    [InlineData("Settings",    "/Settings")]
    public async Task Sidebar_Link_Navigates_And_Highlights(string linkText, string urlSuffix)
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        await page.ClickAsync($".sidebar a.nav-link:has-text('{linkText}')");
        // Some pages (Messages, Topology) default to a tab via query
        // string, so EndsWith the path or contains it counts.
        await page.WaitForURLAsync(
            u => u.Contains(urlSuffix == "/" ? new Uri(app.BaseUrl).AbsolutePath : urlSuffix),
            new PageWaitForURLOptions { Timeout = 5_000 });

        var activeText = (await page.Locator(".sidebar a.nav-link.active").InnerTextAsync()).Trim();
        activeText.Should().Be(linkText, "the visited page should be highlighted in the sidebar");
    }

    [Fact]
    public async Task Theme_Toggle_Flips_Data_Theme_And_Persists()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        // Initial theme is dark per <html data-theme="dark">.
        (await page.GetAttributeAsync("html", "data-theme")).Should().Be("dark");

        await page.ClickAsync("#theme-toggle");
        (await page.GetAttributeAsync("html", "data-theme")).Should().Be("light");
        (await page.EvaluateAsync<string?>("() => localStorage.getItem('dapps-theme')"))
            .Should().Be("light", "theme choice persists in localStorage so the operator's pick survives a reload");

        // Reload + verify the choice survives.
        await page.ReloadAsync();
        (await page.GetAttributeAsync("html", "data-theme")).Should().Be("light");

        // Restore dark so subsequent screenshots / tests aren't biased.
        await page.ClickAsync("#theme-toggle");
        (await page.GetAttributeAsync("html", "data-theme")).Should().Be("dark");
    }

    /// <summary>
    /// Master TX kill-switch one-click roundtrip: Stop -> banner shown,
    /// Resume button -> banner gone. The Stop button is on the topbar;
    /// the Resume button is inside the red banner that the layout
    /// renders when <c>!TxGate.LocalAllowed</c>. Both are plain
    /// form-POSTs that redirect back to the referer.
    /// </summary>
    [Fact]
    public async Task TxKillSwitch_Stop_Shows_Banner_Then_Resume_Clears_It()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        // Stop TX from the topbar. Page reloads to the same URL after
        // the POST redirect; the banner should now exist.
        await page.ClickAsync(".tx-killbtn");
        await page.WaitForSelectorAsync(".tx-banner-stop",
            new PageWaitForSelectorOptions { Timeout = 5_000 });

        var label = (await page.Locator(".tx-banner-stop .label").InnerTextAsync()).Trim();
        label.Should().Be("TX STOPPED", "the banner labels the kill state");

        // Resume from the in-banner button.
        await page.ClickAsync(".tx-banner-stop button[type='submit']");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        (await page.Locator(".tx-banner-stop").CountAsync()).Should().Be(0,
            "resuming TX dismisses the kill banner");
        (await page.Locator(".tx-killbtn").CountAsync()).Should().Be(1,
            "the topbar Stop-TX button is back when transmission is allowed");
    }
}
