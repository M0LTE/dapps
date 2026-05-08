using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Compose page: hand-crafted ihave for protocol debugging. POSTs to
/// itself; on success surfaces a "Last submission" panel with the
/// queued message id, byte count, and on-the-wire preview.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class ComposeTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Compose_Submit_Shows_LastSubmission_Panel()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Compose");

        await page.FillAsync("input#App", "uitest");
        await page.FillAsync("input#Destination", "TEST-1");
        await page.FillAsync("input#Payload", "compose-test-payload");
        await page.FillAsync("input#Ttl", "3600");
        await page.ClickAsync("form.panel button[type='submit']");

        await page.WaitForSelectorAsync(".ok-banner",
            new PageWaitForSelectorOptions { Timeout = 5_000 });

        var preview = await page.Locator(".panel:has(h3:has-text('Last submission')) .value.mono").AllInnerTextsAsync();
        preview.Should().NotBeEmpty();
        // The on-air preview shows "ihave <id> len=<n> dst=<app>@<dst>".
        var ihave = preview.FirstOrDefault(p => p.StartsWith("ihave ", StringComparison.Ordinal));
        ihave.Should().NotBeNull("compose page surfaces the on-air ihave preview after submit");
        ihave!.Should().Contain("dst=uitest@TEST-1");
        ihave.Should().Contain("ttl=3600");
    }

    [Fact]
    public async Task Compose_Validation_Error_On_Bad_Ttl()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Compose");

        await page.FillAsync("input#App", "uitest");
        await page.FillAsync("input#Destination", "TEST-1");
        await page.FillAsync("input#Payload", "ttl-validation");
        await page.FillAsync("input#Ttl", "not-a-number");
        await page.ClickAsync("form.panel button[type='submit']");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var err = await page.Locator(".err-banner").InnerTextAsync();
        err.Should().Contain("TTL", "TTL parse error surfaces in the error banner");
    }
}
