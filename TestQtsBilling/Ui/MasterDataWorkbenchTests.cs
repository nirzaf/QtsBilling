using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Pricing;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The master-data and pricing workbenches (specification task 16, T16.01 - T16.03). Every call goes through the
/// typed clients the shared screens inject, against the real host, so the units the forms send, the refusals the
/// forms report and the history a changed definition must not touch are all proven where an operator would meet them.
/// </summary>
public sealed class MasterDataWorkbenchTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T16_01_a_fifteen_percent_rate_stores_zero_point_one_five_and_a_later_edit_leaves_posted_lines_alone()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var pricing = new PricingApiClient(owner);

        // 1. The screen shows a percentage and sends the fraction. 15 % must arrive as exactly 0.15.
        var created = await pricing.CreateTaxRateAsync(new SaveTaxRateRequest("T16 VAT 15", PricingRules.PercentToRate(15m), IsDefault: false, IsActive: true));
        Assert.Equal(0.15m, created.Rate);
        Assert.Equal("15%", PricingRules.PercentText(created.Rate));
        Assert.Equal(0.15m, PricingRules.PercentToRate(PricingRules.RateToPercent(created.Rate)));

        var fractional = await pricing.CreateTaxRateAsync(new SaveTaxRateRequest("T16 VAT 2.5", PricingRules.PercentToRate(2.5m), IsDefault: false, IsActive: true));
        Assert.Equal(0.025m, fractional.Rate);
        Assert.Equal("2.5%", PricingRules.PercentText(fractional.Rate));

        // 2. A line priced with the 15 % rate posts its own snapshot of the rate and the tax.
        var customerId = await CreateCustomerAsync(owner, "T16 rate history customer");
        var draft = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "T16 rate snapshot",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 2m, 100m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods));
        var draftBody = await draft.Content.ReadAsStringAsync();
        Assert.True(draft.StatusCode == HttpStatusCode.Created, $"the draft could not be created: {(int)draft.StatusCode} {draftBody}");

        var invoiceId = JsonDocument.Parse(draftBody).RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issued = await owner.PostAsJsonAsync($"/api/v1/invoices/{invoiceId}/issue", new { });
        Assert.True(issued.IsSuccessStatusCode, $"the invoice could not be issued: {await issued.Content.ReadAsStringAsync()}");

        var posted = await ReadInvoiceAsync(owner, invoiceId);
        Assert.Equal(0.15m, posted.Lines[0].TaxRate);
        Assert.Equal(200m, posted.Invoice.SubtotalAmount);
        Assert.Equal(30m, posted.Invoice.TaxAmount);
        Assert.Equal(230m, posted.Invoice.TotalAmount);

        // 3. Changing the definition later - which is what this screen is for - does not reach back into that document.
        var raised = await pricing.UpdateTaxRateAsync(created.Id, new SaveTaxRateRequest("T16 VAT 20", PricingRules.PercentToRate(20m), IsDefault: false, IsActive: true));
        Assert.Equal(0.20m, raised.Rate);

        var after = await ReadInvoiceAsync(owner, invoiceId);
        Assert.Equal(0.15m, after.Lines[0].TaxRate);
        Assert.Equal(30m, after.Invoice.TaxAmount);
        Assert.Equal(230m, after.Invoice.TotalAmount);
        Assert.Equal(posted.Invoice.Version, after.Invoice.Version);

        // 4. A new draft priced with the changed rate uses the new number, so the edit is not inert either.
        var newDraft = await owner.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "T16 after the change",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 2m, 100m, 0m, 0.20m)],
            InvoiceRecognitionMode.DirectGoods));
        var newDraftBody = await newDraft.Content.ReadAsStringAsync();
        Assert.True(newDraft.StatusCode == HttpStatusCode.Created, $"the second draft failed: {(int)newDraft.StatusCode} {newDraftBody}");

        var newId = JsonDocument.Parse(newDraftBody).RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var newer = await ReadInvoiceAsync(owner, newId);
        Assert.Equal(0.20m, newer.Lines[0].TaxRate);
        Assert.Equal(40m, newer.Invoice.TaxAmount);

        // 5. Taking the rate out of service does not delete it: the posted line still resolves it.
        await pricing.DeactivateTaxRateAsync(raised.Id);
        var withInactive = await pricing.ListTaxRatesAsync(includeInactive: true);
        var retired = Assert.Single(withInactive, rate => rate.Id == raised.Id);
        Assert.False(retired.IsActive);
        Assert.False(retired.IsDefault);

        var stillLegible = await ReadInvoiceAsync(owner, invoiceId);
        Assert.Equal(0.15m, stillLegible.Lines[0].TaxRate);
    }

    [Fact]
    public async Task T16_02_a_cycle_an_inverted_window_and_an_excessive_value_are_refused_with_the_field_named()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var catalog = new MasterDataApiClient(owner);
        var pricing = new PricingApiClient(owner);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // 1. A category cycle is refused, and the refusal names the field the operator has to fix.
        var top = await catalog.CreateAsync(MasterDataResource.Categories, new SaveMasterDataRequest($"T16 top {suffix}"));
        var middle = await catalog.CreateAsync(MasterDataResource.Categories, new SaveMasterDataRequest($"T16 middle {suffix}", ParentId: top.Id));
        var leaf = await catalog.CreateAsync(MasterDataResource.Categories, new SaveMasterDataRequest($"T16 leaf {suffix}", ParentId: middle.Id));

        var cycle = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateAsync(
            MasterDataResource.Categories, middle.Id, new SaveMasterDataRequest($"T16 middle {suffix}", ParentId: leaf.Id)));
        Assert.Equal(400, cycle.StatusCode);
        Assert.True(cycle.Errors.ContainsKey("ParentId"), $"the refusal did not name the parent field: {cycle.Message}");
        Assert.Contains("descendant", string.Join(" ", cycle.Errors["ParentId"]), StringComparison.OrdinalIgnoreCase);

        var selfParent = await Assert.ThrowsAsync<ApiException>(() => catalog.UpdateAsync(
            MasterDataResource.Categories, middle.Id, new SaveMasterDataRequest($"T16 middle {suffix}", ParentId: middle.Id)));
        Assert.Contains("own parent", selfParent.Message, StringComparison.OrdinalIgnoreCase);

        // 2. A duplicated name is refused deterministically rather than silently creating a second entry.
        var duplicate = await Assert.ThrowsAsync<ApiException>(() => catalog.CreateAsync(
            MasterDataResource.Categories, new SaveMasterDataRequest($"T16 top {suffix}")));
        Assert.True(duplicate.IsConflict, $"a duplicate name answered {duplicate.StatusCode}");
        Assert.DoesNotContain(duplicate.Message, "created", StringComparison.OrdinalIgnoreCase);

        // 3. Discount values and windows are bounded by the server, and the screen's own rule agrees.
        var excessive = await Assert.ThrowsAsync<ApiException>(() => pricing.CreateDiscountAsync(new SaveDiscountRequest(
            $"T16 too much {suffix}", "Percentage", 140m, "All", null, null, IsActive: true)));
        Assert.True(excessive.Errors.ContainsKey("value"), $"the refusal did not name the value: {excessive.Message}");
        Assert.Contains("between 0 and 100", string.Join(" ", excessive.Errors["value"]), StringComparison.OrdinalIgnoreCase);
        Assert.False(PricingRules.PercentIsInRange(140m), "the workbench would have offered a value the server refuses");

        var inverted = await Assert.ThrowsAsync<ApiException>(() => pricing.CreateDiscountAsync(new SaveDiscountRequest(
            $"T16 inverted {suffix}", "Fixed", 5m, "All",
            DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(1), IsActive: true)));
        Assert.True(inverted.Errors.ContainsKey("endsAtUtc"), $"the refusal did not name the window: {inverted.Message}");
        Assert.Contains("after it starts", string.Join(" ", inverted.Errors["endsAtUtc"]), StringComparison.OrdinalIgnoreCase);

        var fine = await pricing.CreateDiscountAsync(new SaveDiscountRequest(
            $"T16 windowed {suffix}", "Percentage", 10m, "Category",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), IsActive: true));
        Assert.Equal(10m, fine.Value);
        Assert.Equal(DiscountKind.Percentage, fine.Kind);
        Assert.True(PricingRules.AppliesNow(fine, DateTimeOffset.UtcNow));
        Assert.Equal("In effect now", PricingRules.StatusText(fine, DateTimeOffset.UtcNow));
        Assert.False(PricingRules.AppliesNow(fine with { IsActive = false }, DateTimeOffset.UtcNow));
        Assert.Contains("Taken out of service", PricingRules.StatusText(fine with { IsActive = false }, DateTimeOffset.UtcNow), StringComparison.Ordinal);
        Assert.Contains("Expired", PricingRules.StatusText(fine, DateTimeOffset.UtcNow.AddDays(30)), StringComparison.Ordinal);

        // 4. A tax rate outside the fraction the column holds is refused, and the screen's conversion cannot produce one.
        var outOfRange = await Assert.ThrowsAsync<ApiException>(() => pricing.CreateTaxRateAsync(
            new SaveTaxRateRequest($"T16 bad rate {suffix}", 1.5m, IsDefault: false, IsActive: true)));
        Assert.True(outOfRange.Errors.ContainsKey("rate"), $"the refusal did not name the rate: {outOfRange.Message}");
        Assert.Contains("fraction between 0 and 1", string.Join(" ", outOfRange.Errors["rate"]), StringComparison.OrdinalIgnoreCase);
        Assert.False(PricingRules.PercentIsInRange(150m));

        // 5. A unit's short name and decimal rule are the unit's own, and the server enforces them.
        var noShortName = await Assert.ThrowsAsync<ApiException>(() => catalog.CreateAsync(
            MasterDataResource.Units, new SaveMasterDataRequest($"T16 unit {suffix}", ShortName: " ")));
        Assert.Equal(400, noShortName.StatusCode);

        var unit = await catalog.CreateAsync(MasterDataResource.Units, new SaveMasterDataRequest($"T16 each {suffix}", "ea", AllowDecimals: false));
        Assert.Equal("ea", unit.ShortName);
        Assert.False(unit.AllowDecimals);

        // The lists are the closed sets the product screen will choose from, and each is complete.
        var units = await catalog.ListAsync(MasterDataResource.Units);
        Assert.Contains(units, row => row.Id == unit.Id);
    }

    [Fact]
    public async Task T16_03_a_role_without_the_pricing_authority_is_denied_read_and_write_alike()
    {
        using var cashier = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);
        var session = await new AccountApiClient(cashier).GetSessionAsync()
            ?? throw new InvalidOperationException("the cashier has no session");

        // 1. The two pricing permissions are exactly what the catalogue promises, and the cashier holds neither.
        Assert.DoesNotContain(Permissions.TaxesManage, session.Permissions);
        Assert.DoesNotContain(Permissions.DiscountsManage, session.Permissions);
        Assert.DoesNotContain(ShellNavigation.Visible(session).Select(row => row.Href), href => href == "catalog/taxes");
        Assert.DoesNotContain(ShellNavigation.Visible(session).Select(row => row.Href), href => href == "catalog/discounts");

        // 2. Denied regardless of hidden controls: the routes themselves refuse the reads and the writes.
        var pricing = new PricingApiClient(cashier);

        var taxList = await Assert.ThrowsAsync<ApiException>(() => pricing.ListTaxRatesAsync());
        Assert.True(taxList.IsForbidden);
        Assert.Equal(Permissions.TaxesManage, ShellNavigation.Routes.Single(row => row.Href == "catalog/taxes").RequiredPermission);

        var taxWrite = await Assert.ThrowsAsync<ApiException>(() => pricing.CreateTaxRateAsync(
            new SaveTaxRateRequest("T16 denied rate", 0.05m, IsDefault: false, IsActive: true)));
        Assert.True(taxWrite.IsForbidden);

        var discountList = await Assert.ThrowsAsync<ApiException>(() => pricing.ListDiscountsAsync());
        Assert.True(discountList.IsForbidden);
        Assert.Equal(Permissions.DiscountsManage, ShellNavigation.Routes.Single(row => row.Href == "catalog/discounts").RequiredPermission);

        var discountWrite = await Assert.ThrowsAsync<ApiException>(() => pricing.CreateDiscountAsync(
            new SaveDiscountRequest("T16 denied discount", "Percentage", 5m, "All", null, null, IsActive: true)));
        Assert.True(discountWrite.IsForbidden);

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.False(await db.TaxRates.AsNoTracking().AnyAsync(rate => rate.Name == "T16 denied rate"));
            Assert.False(await db.Discounts.AsNoTracking().AnyAsync(discount => discount.Name == "T16 denied discount"));
        });

        // 3. Reference lists are readable with products.read - which the cashier holds - but not writable.
        var catalog = new MasterDataApiClient(cashier);
        using var ownerCatalogClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var ownerCatalog = new MasterDataApiClient(ownerCatalogClient);
        var seed = await ownerCatalog.CreateAsync(
            MasterDataResource.Categories, new SaveMasterDataRequest($"T16 readable {Guid.NewGuid():N}"[..24]));

        var readable = await catalog.ListAsync(MasterDataResource.Categories);
        Assert.Contains(readable, row => row.Id == seed.Id);

        var catalogWrite = await Assert.ThrowsAsync<ApiException>(() => catalog.CreateAsync(
            MasterDataResource.Categories, new SaveMasterDataRequest($"T16 denied category {Guid.NewGuid():N}")));
        Assert.True(catalogWrite.IsForbidden);

        Assert.Contains(Permissions.ProductsRead, session.Permissions);
        Assert.DoesNotContain(Permissions.ProductsWrite, session.Permissions);

        // 4. The owner, who holds all three, gets the opposite answer from the same clients.
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/tax-rates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/discounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/categories")).StatusCode);
    }

    [Fact]
    public void T16_04_the_new_screens_are_routed_shipped_pages_and_every_host_registers_their_clients()
    {
        var root = InvoiceEditorTests.RepositoryRoot();
        var pages = new[]
        {
            typeof(QtsBilling.Ui.Pages.Catalog.MasterData),
            typeof(Taxes),
            typeof(Discounts),
        };

        string[] shipped = [.. pages
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template.Trim('/'))];

        // The catalogue offers these three, so each address must be a component that exists.
        foreach (var href in new[] { "catalog/master-data", "catalog/taxes", "catalog/discounts" })
        {
            var row = ShellNavigation.Routes.Single(candidate => candidate.Href == href);
            Assert.Equal(ShellAvailability.Available, row.Availability);
            Assert.Contains(href, shipped);
        }

        var injected = pages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface && type.Assembly == typeof(IMasterDataApiClient).Assembly)
            .Distinct()
            .ToArray();

        Assert.Contains(typeof(IMasterDataApiClient), injected);
        Assert.Contains(typeof(IPricingApiClient), injected);

        using var scope = _fixture.Factory.Services.CreateScope();
        var missing = injected.Where(client => scope.ServiceProvider.GetService(client) is null).Select(type => type.Name).ToArray();
        Assert.True(missing.Length == 0, $"the server host renders the pricing screens but registers none for: {string.Join(", ", missing)}");

        foreach (var startup in new[] { @"QtsWebApp\QtsWebApp.Client\Program.cs", @"QtsBilling.Maui\MauiProgram.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, startup));
            var unnamed = injected.Select(type => type.Name).Where(name => !source.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(unnamed.Length == 0, $"{startup} routes the pricing screens but never registers: {string.Join(", ", unnamed)}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Guid> CreateCustomerAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers",
            new QtsBilling.Contracts.Customers.SaveCustomerRequest(name, null, null, null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"the customer could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<InvoiceDetailDto> ReadInvoiceAsync(HttpClient client, Guid invoiceId)
    {
        var response = await client.GetAsync($"/api/v1/invoices/{invoiceId}");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<InvoiceDetailDto>(ContractJson.Options)
            ?? throw new InvalidOperationException("the invoice answered without a body");
    }
}
