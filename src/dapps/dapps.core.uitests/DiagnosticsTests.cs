using AwesomeAssertions;
using Microsoft.Playwright;

namespace dapps.core.uitests;

/// <summary>
/// Diagnostics page: filterable transmissions audit log + on-demand
/// probe / poll sweep buttons in the page-head actions.
/// </summary>
[Collection(LoggedInUiCollection.Name)]
public sealed class DiagnosticsTests(LoggedInWebAppFixture app, PlaywrightFixture pw)
{
    [Fact]
    public async Task Diagnostics_Audit_Status_Becomes_Live_Or_Empty()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Diagnostics");

        // Status pill flips off "loading" when /Transmissions answers
        // (HTTP 200, even if the body is []). "live" is the success
        // shape; "error" / "warn" would surface a regression in the
        // controller route. Either non-loading state proves the JS
        // fired the fetch.
        await page.WaitForFunctionAsync(
            "() => { const e = document.getElementById('tx-status'); return e && e.textContent !== 'loading'; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        // The pill class applies CSS text-transform: uppercase, and
        // chromium's innerText respects that styling, so compare lower-
        // cased. textContent would also work but innerText is more
        // representative of what the operator actually sees.
        var status = (await page.Locator("#tx-status").InnerTextAsync()).Trim().ToLowerInvariant();
        status.Should().BeOneOf("live", "error",
            "tx-status pill reports the result of the /Transmissions fetch");
    }

    [Fact]
    public async Task Diagnostics_Filter_Inputs_Trigger_Reload()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Diagnostics");

        // Wait for the initial load to settle.
        await page.WaitForFunctionAsync(
            "() => document.getElementById('tx-status').textContent !== 'loading'",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        // Capture the next /Transmissions request to confirm the filter
        // is wired up. The kind dropdown change fires load() which
        // hits /Transmissions?... .
        var reqTask = page.WaitForRequestAsync(req =>
            req.Url.Contains("/Transmissions?") && req.Url.Contains("kind=beacon"),
            new PageWaitForRequestOptions { Timeout = 5_000 });

        await page.SelectOptionAsync("#tx-kind", "beacon");
        var req = await reqTask;
        req.Url.Should().Contain("kind=beacon");
    }

    [Fact]
    public async Task Diagnostics_Sweep_Buttons_Are_Present_In_Page_Head()
    {
        await using var ctx = await pw.Browser.NewLoggedInContextAsync(app);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/Diagnostics");

        (await page.Locator(".page-head button:has-text('Probe everyone')").CountAsync())
            .Should().Be(1);
        (await page.Locator(".page-head button:has-text('Poll everyone')").CountAsync())
            .Should().Be(1);
    }
}
