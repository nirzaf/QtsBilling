using System.Reflection;
#if QTS_BROWSER_TESTS
using Microsoft.Playwright;
#endif
using Microsoft.Extensions.DependencyInjection;
using TestQtsBilling.Integration;
using QtsBilling.Ui.Pages.Invoices;

namespace TestQtsBilling.Ui;

/// <summary>
/// The invoice screens must activate wherever they render (specification task 02, T02.05). A shared page only
/// injects typed clients, so the host that forgot to register one shows the operator a dead form instead of a
/// working screen — the exact defect (finding F25) these checks retire.
/// </summary>
/// <remarks>
/// The server branch is proven against the real host container. The browser and MAUI branches are proven from
/// their own startup source, because neither host can be started inside this test process; the interactive
/// journeys over all three hosts belong to the browser fixture and Task 45.
/// </remarks>
public sealed class InvoiceEditorTests : IClassFixture<PostgresApiFixture>
{
    private static readonly Type[] InvoicePages =
    [
        typeof(InvoiceEditor),
        typeof(QtsBilling.Ui.Pages.Invoices.Invoices),
    ];

    private readonly PostgresApiFixture _fixture;

    public InvoiceEditorTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void T02_05_the_server_host_resolves_every_client_the_invoice_pages_inject()
    {
        using var scope = _fixture.Factory.Services.CreateScope();

        var missing = InjectedClientTypes()
            .Where(client => scope.ServiceProvider.GetService(client) is null)
            .Select(type => type.Name)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"the server host renders the invoice pages but registers no implementation for: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData(@"QtsWebApp\QtsWebApp.Client\Program.cs")]
    [InlineData(@"QtsBilling.Maui\MauiProgram.cs")]
    public void T02_05_every_host_that_renders_the_invoice_pages_registers_their_clients(string startupFile)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), startupFile));

        var missing = InjectedClientTypes()
            .Select(type => type.Name)
            .Where(name => !source.Contains(name, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{startupFile} renders the invoice pages but never registers: {string.Join(", ", missing)}");
    }

    [Fact]
    public void T02_05_the_shared_page_assembly_holds_no_persistence_or_database_reference()
    {
        var offenders = typeof(InvoiceEditor).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"the shared UI reached the database layer: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void T02_05_the_browser_client_registers_no_database_service()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), @"QtsWebApp\QtsWebApp.Client\Program.cs"));

        Assert.DoesNotContain("EntityFrameworkCore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The feature interfaces the invoice pages ask their host to supply. Only the typed contracts count here:
    /// a component-library service such as the dialog service arrives with the MudBlazor registration, so
    /// requiring it in a startup file would test the framework rather than the host.
    /// </summary>
    private static IReadOnlyList<Type> InjectedClientTypes()
        => [.. InvoicePages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<Microsoft.AspNetCore.Components.InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface && type.Assembly == typeof(QtsBilling.Contracts.Invoices.IInvoiceApiClient).Assembly)
            .Distinct()];

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QtsBilling.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The test cannot locate the repository root from its own output directory.");
    }
}

#if QTS_BROWSER_TESTS
/// <summary>
/// The invoice journeys as the operator actually performs them (specification task 02, T02.01 - T02.04) inside
/// a real browser against the owned loopback host and a marked disposable database.
/// </summary>
/// <remarks>
/// Compiled only when the test project is built with <c>-p:QtsBrowserTests=true</c>, so a run without the
/// preloaded browser kit can never report a browser pass it did not earn.
/// </remarks>
[Collection(nameof(BrowserHarnessCollection))]
public sealed class InvoiceEditorJourneys(BrowserFixture browser)
{
    [Fact]
    public async Task T02_01_an_operator_with_issue_permission_can_issue_a_draft_once()
    {
        var page = await browser.CreateSignedInPageAsync(browser.Owner).ConfigureAwait(false);

        await page.GotoAsync($"{browser.Origin}/invoices/{browser.DraftInvoiceId:D}").ConfigureAwait(false);

        var issue = page.GetByRole(AriaRole.Button, new() { Name = "Issue invoice" });
        Assert.Equal(1, await issue.CountAsync().ConfigureAwait(false));

        await issue.ClickAsync().ConfigureAwait(false);

        // The issued document leaves the editable form; a second issue is no longer offered.
        await page.WaitForSelectorAsync("text=Draft fields are locked after issue").ConfigureAwait(false);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).CountAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task T02_01_a_reader_sees_the_details_without_an_editable_form()
    {
        var page = await browser.CreateSignedInPageAsync(browser.Cashier).ConfigureAwait(false);

        await page.GotoAsync($"{browser.Origin}/invoices/{browser.DraftInvoiceId:D}").ConfigureAwait(false);

        // The cashier may not maintain invoices: no editable form is handed over, and the reason is stated.
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).CountAsync().ConfigureAwait(false));
        Assert.Contains("cannot", await page.ContentAsync().ConfigureAwait(false), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T02_02_a_percent_tax_input_round_trips_as_a_fraction_and_back()
    {
        var page = await browser.CreateSignedInPageAsync(browser.Owner).ConfigureAwait(false);

        await page.GotoAsync($"{browser.Origin}/invoices/{browser.DraftInvoiceId:D}").ConfigureAwait(false);

        // The stored line carries 0.15; the form edits percent units and the server receives a fraction again.
        var taxField = page.Locator(".mud-input-control", new() { HasText = "Tax %" }).Locator("input");
        Assert.Equal("15.00", await taxField.InputValueAsync().ConfigureAwait(false));

        await taxField.FillAsync("20").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync().ConfigureAwait(false);

        await page.WaitForURLAsync($"**/invoices/{browser.DraftInvoiceId:D}").ConfigureAwait(false);
        await page.ReloadAsync().ConfigureAwait(false);
        Assert.Equal("20.00", await page.Locator(".mud-input-control", new() { HasText = "Tax %" }).Locator("input").InputValueAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task T02_03_an_obsolete_search_response_cannot_replace_the_current_list()
    {
        var page = await browser.CreateSignedInPageAsync(browser.Owner).ConfigureAwait(false);

        await page.GotoAsync($"{browser.Origin}/invoices").ConfigureAwait(false);

        // Stall the first query, then change the filter: the earlier response must not repaint the list.
        await page.RouteAsync("**/api/v1/invoices**", async route =>
        {
            await Task.Delay(1200).ConfigureAwait(false);
            await route.ContinueAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        await page.GetByLabel("Invoice number or customer").FillAsync("ACME").ConfigureAwait(false);
        await page.GetByLabel("Invoice number or customer").FillAsync("GLOBEX").ConfigureAwait(false);

        await browser.UnstallRequestDelay(page).ConfigureAwait(false);
        var visible = await page.Locator(".mud-table-body").InnerTextAsync().ConfigureAwait(false);

        Assert.Contains("GLOBEX", visible, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stale-search-marker", visible, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T02_04_editing_terms_never_changes_the_recognition_mode()
    {
        var page = await browser.CreateSignedInPageAsync(browser.Owner).ConfigureAwait(false);

        await page.GotoAsync($"{browser.Origin}/invoices/{browser.DirectGoodsInvoiceId:D}").ConfigureAwait(false);

        await page.GetByLabel("Note").FillAsync("Terms edited in the browser journey").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync().ConfigureAwait(false);

        await page.ReloadAsync().ConfigureAwait(false);
        Assert.Contains("direct-goods", await page.ContentAsync().ConfigureAwait(false), StringComparison.OrdinalIgnoreCase);
    }
}
#endif
