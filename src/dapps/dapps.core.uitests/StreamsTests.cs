using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Streams page: per-(remote, stream-id) send/receive cursors. Pure
/// server-rendered table, so empty-state coverage is enough.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class StreamsTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Streams_Empty_State_Renders_Both_Halves()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Streams");

        var headings = await page.Locator(".panel h3").AllInnerTextsAsync();
        headings.Should().Contain("Send state");
        headings.Should().Contain("Receive state");

        // On a fresh node neither half has any rows; the model emits
        // the .empty placeholder.
        (await page.Locator(".panel .empty").CountAsync())
            .Should().Be(2, "Send and Receive panels each emit an empty placeholder when no streams exist");
    }
}
