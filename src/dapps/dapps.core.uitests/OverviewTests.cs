using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Overview page (/): hero status row, sparkline + decision ticker,
/// queue summary, "needs attention" panel, and the quick-send form.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class OverviewTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Overview_Hero_Tiles_All_Render()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        // Four hero tiles: node, MQTT, TX gate, build.
        foreach (var id in new[] { "hero-node", "hero-mqtt", "hero-tx", "hero-update" })
        {
            (await page.Locator($"#{id}").CountAsync())
                .Should().Be(1, $"#{id} hero tile is on the overview");
        }

        // Sparkline + ticker + the three queue/airtime panels.
        (await page.Locator("#spark").CountAsync()).Should().Be(1);
        (await page.Locator("#ticker").CountAsync()).Should().Be(1);
        (await page.Locator("#alerts").CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Overview_Quick_Send_Submits_And_Banners_Success()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        // The topbar carries its own form[method='post'] (the TX-stop
        // killbutton), so target the in-page panel form by its visible
        // submit-button text rather than a generic form selector.
        await page.FillAsync("input#App", "uitest");
        await page.FillAsync("input#Destination", "TEST-1");
        await page.FillAsync("input#Payload", "hello from quick-send");
        await page.ClickAsync("button[type='submit']:has-text('Submit to queue')");

        await page.WaitForSelectorAsync(".ok-banner",
            new PageWaitForSelectorOptions { Timeout = 5_000 });
        var banner = await page.Locator(".ok-banner").InnerTextAsync();
        banner.Should().Contain("Queued message",
            "successful submission shows a Queued message <id> for <app>@<dest> banner");
    }

    /// <summary>
    /// Recent decisions ticker renders OperationalEvents from the
    /// snapshot. The wire shape is { at, kind, summary } - the previous
    /// JS read { atUtc, kind, detail }, which left the time column
    /// stuck on "?" and the message column blank for every event.
    /// Stubbing /Operational with a known event lets us assert against
    /// the *rendered* time and summary strings.
    /// </summary>
    [Fact]
    public async Task Overview_Recent_Decisions_Renders_Time_And_Summary()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();

        // 12:34:56 UTC - the ticker formats as HH:mm:ss so we can match
        // the cell text exactly.
        var eventAt = new DateTime(2026, 5, 9, 12, 34, 56, DateTimeKind.Utc);
        var snap = new
        {
            callsign = "M0LTE-7",
            version = "0.33.6",
            generatedAt = DateTime.UtcNow,
            uptimeSeconds = 0,
            status = "healthy",
            callsignConfigured = true,
            nodeReachable = true,
            mqttBrokerUp = true,
            forwardSuccess = 0,
            pendingOutboundCount = 0,
            undeliveredLocalCount = 0,
            totalMessagesCount = 0,
            neighbourCount = 0,
            discoveredPeerCount = 0,
            discoveryChannelCount = 0,
            airtimeConsumedSecondsLastHour = 0,
            airtimeBudgetSecondsPerHour = 0,
            recentEvents = new[]
            {
                new { at = eventAt, kind = "agw.reconnect", summary = "AGW socket connected + 'X' registered" },
            },
            neighbourLinks = Array.Empty<object>(),
            tables = new
            {
                outbound = Array.Empty<object>(),
                localInbox = Array.Empty<object>(),
                dropped = Array.Empty<object>(),
                neighbours = Array.Empty<object>(),
                discoveredPeers = Array.Empty<object>(),
                discoveryChannels = Array.Empty<object>(),
                probedNodes = Array.Empty<object>(),
                polledNodes = Array.Empty<object>(),
                update = (object?)null,
            },
        };
        var json = JsonSerializer.Serialize(snap);
        await page.RouteAsync("**/Operational*", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = json,
            });
        });

        await page.GotoAsync(app.BaseUrl);
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#ticker .row .k')?.textContent === 'agw.reconnect'",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        var time = await page.Locator("#ticker .row .t").First.InnerTextAsync();
        time.Trim().Should().Be("12:34:56", "the time column shows the event's HH:mm:ss");

        var msg = await page.Locator("#ticker .row .m").First.InnerTextAsync();
        msg.Should().Contain("AGW socket connected", "the summary text should render alongside the kind");
    }

    /// <summary>
    /// The Outbound queue / Local inbox / Discovery airtime panels
    /// share a row in .grid-3 and should have equal heights so the
    /// row reads as one visual unit. The default browser grid stretch
    /// wasn't applying in practice (different intrinsic content
    /// heights per panel); the explicit `height: 100%; display: flex`
    /// rule on grid-3 children makes them line up.
    /// </summary>
    [Fact]
    public async Task Overview_Queue_Summary_Panels_Have_Equal_Height()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app,
            new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 900 } });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);
        await page.WaitForTimeoutAsync(800); // let the snapshot land + the JS render the panels

        var heights = await page.EvaluateAsync<double[]>(@"() =>
            Array.from(document.querySelectorAll('.grid-3 > .panel'))
                .map(p => p.getBoundingClientRect().height)");

        heights.Should().HaveCountGreaterThanOrEqualTo(3,
            "the queue summary row exposes outbound / inbox / airtime panels");
        var min = heights.Min();
        var max = heights.Max();
        (max - min).Should().BeLessThan(2.0,
            $"all panels in the same .grid-3 row should be equal height; saw {string.Join(", ", heights)}");
    }

    [Fact]
    public async Task Overview_Quick_Send_Validation_Shows_Error_Banner()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        // The quick-send form has required attributes on all three
        // fields, so to land on the server-side validation we have to
        // bypass the required-attribute path. Submit via JS form.submit()
        // to skip the browser's HTML5 validation gate. Target the
        // in-page panel form specifically - the topbar TX-stop form is
        // the first form[method='post'] in DOM order and would otherwise
        // be the one we submit.
        await page.EvaluateAsync(
            "() => document.querySelector(\"form.panel[method='post']\").submit()");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        (await page.Locator(".err-banner").CountAsync()).Should().Be(1,
            "blank submit lands on the server-rendered error banner");
    }
}
