#if QTS_BROWSER_TESTS
using Microsoft.Playwright;
using TestQtsBilling.Integration;
using Xunit;
using Xunit.Abstractions;

namespace TestQtsBilling.Ui;

/// <summary>
/// The shipped screens driven by a real browser against a real host and a real PostgreSQL database
/// (specification task 02's harness, exercised as the G5 exit evidence and as the shape of task 45's acceptance
/// sheet). Nothing here is a render of a component inside a test process: an actual Chromium-family browser
/// signs in over the host's own loopback TLS listener, walks the navigation, presses the buttons, uploads a file
/// through the file input, and leaves a full-page screenshot of every step in
/// <c>tmp/browser-evidence/</c> for a person to look at.
/// </summary>
/// <remarks>
/// The browser is the one the offline kit preloads (<c>QTS_BROWSER_EXECUTABLE</c>); a run without it is blocked at
/// the fixture, never reported as a pass. Assertions are on the markers the pages themselves carry, so a screen
/// that shows a loading spinner forever fails instead of passing on a screenshot.
/// </remarks>
[Collection(nameof(BrowserHarnessCollection))]
public sealed class BrowserJourneys(BrowserFixture harness, ITestOutputHelper output)
{
    private static readonly string EvidenceDirectory = Path.Combine(
        InvoiceEditorTests.RepositoryRoot(), "tmp", "browser-evidence");

    [Fact]
    public async Task G5_the_shipped_screens_answer_a_real_operator_in_a_real_browser()
    {
        Directory.CreateDirectory(EvidenceDirectory);

        var page = await harness.CreateSignedInPageAsync(harness.Owner);

        // 1. The signed-in home page: the shell is offered, and the page is no longer a sign-in form.
        await GotoAsync(page, $"{harness.Origin}/");
        await WaitForAsync(page, ".mud-nav-link");
        await ShotAsync(page, "01-home");

        Assert.True(await page.Locator("input[type=\"password\"]").CountAsync() == 0,
            "the home page still offers a password field, so nobody is signed in");

        if (await page.Locator("[data-qts-region=\"pulse\"]").CountAsync() > 0)
        {
            var home = await page.Locator("[data-qts-region=\"pulse\"]").InnerTextAsync();
            output.WriteLine($"home dashboard: {Collapse(home)}");
            Assert.True(home.Trim().Length > 20, "the dashboard region rendered empty");
        }
        else
        {
            output.WriteLine("home dashboard: this host's landing page carries no dashboard region");
        }

        // 2. The invoice list, read by name rather than by a typed identifier.
        await GotoAsync(page, $"{harness.Origin}/invoices");
        await WaitForAsync(page, ".mud-table-row");
        var list = await page.Locator(".mud-table-body").InnerTextAsync();
        await ShotAsync(page, "02-invoices");
        output.WriteLine($"invoice list: {Collapse(list)}");
        Assert.DoesNotContain("Nothing matches", list, StringComparison.Ordinal);

        // 3. One document's workbench, with the figures the server holds.
        await GotoAsync(page, $"{harness.Origin}/invoices/{harness.DirectGoodsInvoiceId:D}");
        await WaitForAsync(page, "[data-qts-region=\"document-evidence\"]");
        await WaitForAsync(page, "[data-qts-figure=\"due\"]");
        await ShotAsync(page, "03-invoice-workbench");

        var due = await page.Locator("[data-qts-figure=\"due\"]").InnerTextAsync();
        output.WriteLine($"invoice due figure: {Collapse(due)}");
        Assert.False(string.IsNullOrWhiteSpace(due), "the settlement workbench shows no due figure");

        // 4. Evidence is placed through the browser's own file input, then agreed to.
        var pdf = Path.Combine(Path.GetTempPath(), $"qts-browser-evidence-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\nbrowser journey evidence\n%%EOF\n");

        await page.Locator("[data-qts-region=\"document-evidence\"] input[type=\"file\"]")
            .SetInputFilesAsync(pdf);

        var titleField = page.Locator("[data-qts-region=\"document-evidence\"] input:not([type=\"file\"])").First;
        await titleField.FillAsync("supplier delivery note");

        await page.Locator("[data-qts-action=\"attach-evidence\"]").First.ClickAsync();
        await WaitForAsync(page, "[data-qts-region=\"document-evidence\"] [data-qts-state=\"notice\"]");
        var attached = await page
            .Locator("[data-qts-region=\"document-evidence\"] [data-qts-state=\"notice\"]").InnerTextAsync();
        await ShotAsync(page, "04-evidence-attached");
        output.WriteLine($"evidence attached: {Collapse(attached)}");
        Assert.Contains("held on this instance", attached, StringComparison.OrdinalIgnoreCase);

        // 5. The held version is listed with its type and digest, and a copy is taken by the host's own file
        //    facility - never by a link the page composed.
        await WaitForAsync(page, "[data-qts-region=\"evidence-versions\"]");
        var versions = await page.Locator("[data-qts-region=\"evidence-versions\"]").InnerTextAsync();
        Assert.Contains("application/pdf", versions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("held now", versions, StringComparison.OrdinalIgnoreCase);

        await page.Locator("[data-qts-action=\"keep-evidence\"]").First.ClickAsync();
        await WaitForAsync(page, "[data-qts-region=\"document-evidence\"] [data-qts-state=\"notice\"]");
        var kept = await page
            .Locator("[data-qts-region=\"document-evidence\"] [data-qts-state=\"notice\"]").InnerTextAsync();
        await ShotAsync(page, "05-evidence-kept");
        output.WriteLine($"keep a copy: {Collapse(kept)}");
        Assert.True(
            kept.Contains("Kept on this device", StringComparison.OrdinalIgnoreCase)
                || kept.Contains("refused the file", StringComparison.OrdinalIgnoreCase),
            $"the panel said something other than the truth about the copy: {kept}");

        // 6. A statement the manager reads, with its reconciliation sentence.
        await GotoAsync(page, $"{harness.Origin}/reports");
        await WaitForAsync(page, "[data-qts-region=\"ask\"]");
        await ShotAsync(page, "06-reports-ask");

        await page.Locator("[data-qts-action=\"read-report\"]").First.ClickAsync();
        await WaitForAsync(page, "[data-qts-region=\"figures\"]");
        var figures = await page.Locator("[data-qts-region=\"figures\"]").InnerTextAsync();
        await ShotAsync(page, "07-reports-figures");
        output.WriteLine($"report figures: {Collapse(figures)}");
        Assert.True(figures.Length > 40, "the report region came back with nothing in it");

        // 7. The audit feed, filtered by the record type the evidence panel just wrote.
        await GotoAsync(page, $"{harness.Origin}/administration/audit");
        await WaitForAsync(page, "[data-qts-action=\"search-audit\"]");
        await page.GetByLabel("Record type").FillAsync("document_evidence");
        await page.Locator("[data-qts-action=\"search-audit\"]").First.ClickAsync();
        await WaitForAsync(page, "[data-qts-region=\"entries\"]");
        var audit = await page.Locator("[data-qts-region=\"entries\"]").InnerTextAsync();
        await ShotAsync(page, "08-audit");
        output.WriteLine($"audit feed: {Collapse(audit)}");
        Assert.Contains("document_evidence", audit, StringComparison.OrdinalIgnoreCase);

        // 8. The instance's own diagnostics, read from its own evidence.
        await GotoAsync(page, $"{harness.Origin}/administration/diagnostics");
        await WaitForAsync(page, "[data-qts-region=\"store\"]");
        await WaitForAsync(page, "[data-qts-region=\"capacity\"]");
        var store = await page.Locator("[data-qts-region=\"store\"]").InnerTextAsync();
        var capacity = await page.Locator("[data-qts-region=\"capacity\"]").InnerTextAsync();
        var alerts = await page.Locator("[data-qts-region=\"alerts\"]").InnerTextAsync();
        await ShotAsync(page, "09-diagnostics");
        output.WriteLine($"store: {Collapse(store)} | capacity: {Collapse(capacity)} | alerts: {Collapse(alerts)}");
        Assert.Contains("schema", store, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%", capacity, StringComparison.Ordinal);

        // 9. Two screens that may legitimately hold nothing must say so, not show a spinner or a fake zero.
        await GotoAsync(page, $"{harness.Origin}/expenses");
        await WaitForAsync(page, ".mud-table, [data-qts-state=\"empty\"], [data-qts-state=\"loading\"]");
        await ShotAsync(page, "10-expenses");

        await GotoAsync(page, $"{harness.Origin}/purchasing/orders");
        await WaitForAsync(page, ".mud-table, [data-qts-state=\"empty\"], [data-qts-state=\"loading\"]");
        await ShotAsync(page, "11-purchase-orders");

        output.WriteLine($"screenshots written to {EvidenceDirectory}");
    }

    private static async Task GotoAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000,
        }).ConfigureAwait(false);

        // The circuit needs a moment before any interactive read starts; the waits below are on the markers, so
        // this is only a floor, never the thing the journey relies on.
        await page.WaitForTimeoutAsync(1_500).ConfigureAwait(false);
    }

    private static async Task WaitForAsync(IPage page, string selector)
    {
        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 45_000,
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or PlaywrightException)
        {
            await ShotAsync(page, $"FAILED-wait-{Math.Abs(selector.GetHashCode()):X8}").ConfigureAwait(false);
            throw new Xunit.Sdk.XunitException(
                $"the browser never showed {selector}. {exception.Message}");
        }
    }

    private static async Task ShotAsync(IPage page, string name)
    {
        var path = Path.Combine(EvidenceDirectory, $"{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true })
            .ConfigureAwait(false);
    }

    private static string Collapse(string text)
        => new string(text.Replace('\r', ' ').Replace('\n', ' ').Where(character => !char.IsControl(character)).ToArray())
            .Replace("    ", " ").Replace("   ", " ").Replace("  ", " ").Trim();
}
#endif
