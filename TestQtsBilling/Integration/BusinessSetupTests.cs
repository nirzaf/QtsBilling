using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Business and branch setup as the owner performs it (specification task 06, T06.01 - T06.03). Nothing here
/// reaches for SQL or a development seeder: identity, time zone, fiscal month, numbering and branches are all
/// commands, and the guards that protect history from a careless PUT are proven to hold.
/// </summary>
/// <remarks>
/// The class shares one fixture database, so every assertion is written against the state this test itself
/// created or re-read, never against a global starting condition.
/// </remarks>
public sealed class BusinessSetupTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public BusinessSetupTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T06_01_the_owner_completes_setup_through_commands_and_versions_advance()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        await CompleteAccountingSetupAsync(client);

        var created = await client.PostAsJsonAsync("/api/v1/locations", new CreateLocationRequest(
            "Second till room",
            $"SECOND-{Guid.NewGuid():N}"[..16],
            "Unit 4, Market Street",
            "Europe/Berlin"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = await created.Content.ReadFromJsonAsync<BusinessLocationView>(ContractJson.Options);
        Assert.NotNull(location);
        Assert.Equal(1, location.Version);

        var renamed = await client.PutAsJsonAsync($"/api/v1/locations/{location.Id}", new UpdateLocationRequest(
            ExpectedVersion: 1,
            Name: "Second till room (north)",
            Code: location.Code,
            Address: "Unit 5, Market Street",
            TimeZone: "Europe/Berlin"));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedDto = await renamed.Content.ReadFromJsonAsync<BusinessLocationView>(ContractJson.Options);
        Assert.Equal(2, renamedDto!.Version);
        Assert.Equal("Second till room (north)", renamedDto.Name);

        var before = await GetSettingsAsync(client);
        var saved = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(
            before,
            name: "Setup Verified Trading Co",
            numbering: new NumberingSettingsDto("SVC-", 4)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await GetSettingsAsync(client);
        Assert.Equal("Setup Verified Trading Co", after.Name);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(new NumberingSettingsDto("SVC-", 4), after.Numbering);
        Assert.True(after.Setup.IsReadyToPost, string.Join(" ", after.Setup.BlockingReasons));

        // The number series the owner chose is what the ledger stores, and the decision is audited. A branch
        // command writes its feed row in the same transaction as the row itself.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var settings = await db.BusinessAccountingSettings.AsNoTracking()
                .SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId);
            Assert.Equal("SVC-", settings.InvoicePrefix);
            Assert.Equal(4, settings.InvoicePadding);

            Assert.True(await db.AuditEvents.AnyAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                && entry.EntityType == "business_settings"
                && entry.Action == "updated"));

            Assert.True(await db.AuditEvents.AnyAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                && entry.EntityType == "business_location"
                && entry.EntityId == location.Id
                && entry.Action == "created"));

            Assert.True(await db.SyncChanges.AnyAsync(change => change.BusinessId == _fixture.BusinessA.BusinessId
                && change.EntityType == "location"
                && change.EntityId == location.Id));
        });
    }

    [Fact]
    public async Task T06_02_posted_facts_freeze_financial_identity_and_a_used_location_is_never_deleted()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        await CompleteAccountingSetupAsync(client);
        await PostOpeningJournalAsync(client);

        var current = await GetSettingsAsync(client);

        var currency = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(current, baseCurrency: "USD"));
        await AssertRefusedAsync(currency, "base currency");

        var fiscal = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(current, fiscalYearStartMonth: current.FiscalYearStartMonth == 7 ? 8 : 7));
        await AssertRefusedAsync(fiscal, "fiscal year start month");

        var scale = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(current, settlementScale: current.SettlementScale == 4 ? 3 : 4));
        await AssertRefusedAsync(scale, "settlement scale");

        var unchanged = await GetSettingsAsync(client);
        Assert.Equal(current.Version, unchanged.Version);
        Assert.Equal(current.BaseCurrency, unchanged.BaseCurrency);
        Assert.Equal(current.FiscalYearStartMonth, unchanged.FiscalYearStartMonth);

        // A branch with posted history is deactivated, never deleted: the route does not exist and the row stays.
        var delete = await client.DeleteAsync($"/api/v1/locations/{_fixture.BusinessA.LocationId}");
        Assert.True(
            delete.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"a location must not be deletable, but DELETE answered {(int)delete.StatusCode}");

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.True(await db.BusinessLocations.AnyAsync(row => row.Id == _fixture.BusinessA.LocationId
                && row.BusinessId == _fixture.BusinessA.BusinessId));
        });
    }

    [Fact]
    public async Task T06_03_a_stale_version_or_invalid_payload_refuses_without_a_partial_write()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        var before = await GetSettingsAsync(client);
        var auditBefore = await CountBusinessSettingsAuditAsync();

        var stale = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(
            before,
            expectedVersion: before.Version + 9,
            name: "Overwritten by a stale editor"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("CONCURRENCY_CONFLICT", staleBody.RootElement.GetProperty("code").GetString());

        var badZone = await client.PostAsJsonAsync("/api/v1/locations", new CreateLocationRequest("Imaginary branch", "MARS", null, "Mars/Phobos"));
        Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);

        var badMonth = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(before, name: "x", fiscalYearStartMonth: 13));
        Assert.Equal(HttpStatusCode.BadRequest, badMonth.StatusCode);

        var badPrefix = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(before, numbering: new NumberingSettingsDto("### not a prefix ###", 2)));
        Assert.Equal(HttpStatusCode.BadRequest, badPrefix.StatusCode);

        var after = await GetSettingsAsync(client);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.FiscalYearStartMonth, after.FiscalYearStartMonth);
        Assert.Equal(auditBefore, await CountBusinessSettingsAuditAsync());

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.False(await db.BusinessLocations.AnyAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId
                && row.Code == "MARS"));
        });
    }

    [Fact]
    public async Task T06_01_a_fresh_business_is_told_what_is_missing_and_then_sets_its_fiscal_month()
    {
        // Business B is the untouched control: it has a chart and a period but no saved control-account mapping.
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerB);

        var blocked = await GetSettingsAsync(client);
        Assert.False(blocked.Setup.IsReadyToPost);
        Assert.NotEmpty(blocked.Setup.BlockingReasons);
        Assert.Contains(blocked.Setup.BlockingReasons, reason => reason.Contains("control accounts", StringComparison.OrdinalIgnoreCase));

        await CompleteAccountingSetupAsync(client);

        // Before any fact is posted, the fiscal boundary is the owner's to choose.
        var current = await GetSettingsAsync(client);
        Assert.True(current.Setup.IsReadyToPost, string.Join(" ", current.Setup.BlockingReasons));

        var saved = await client.PutAsJsonAsync("/api/v1/business-settings", SaveOf(
            current,
            name: current.Name + " (April year)",
            fiscalYearStartMonth: 4));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await GetSettingsAsync(client);
        Assert.Equal(4, after.FiscalYearStartMonth);
        Assert.Equal(current.Version + 1, after.Version);
    }

    // ------------------------------------------------------------------ helpers

    private static SaveBusinessSettingsRequest SaveOf(
        BusinessSettingsDto current,
        long? expectedVersion = null,
        string? name = null,
        string? baseCurrency = null,
        int? fiscalYearStartMonth = null,
        int? settlementScale = null,
        NumberingSettingsDto? numbering = null)
        => new(
            ExpectedVersion: expectedVersion ?? current.Version,
            Name: name ?? current.Name,
            LegalName: current.LegalName,
            TimeZone: current.TimeZone,
            FiscalYearStartMonth: fiscalYearStartMonth ?? current.FiscalYearStartMonth,
            BaseCurrency: baseCurrency ?? current.BaseCurrency,
            SettlementScale: settlementScale ?? current.SettlementScale,
            Numbering: numbering ?? current.Numbering);

    private async Task<BusinessSettingsDto> GetSettingsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/business-settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<BusinessSettingsDto>(ContractJson.Options);
        return dto ?? throw new InvalidOperationException("business-settings returned an empty body");
    }

    /// <summary>Maps the seeded chart onto the control-account slots, the way an owner would from the screens.</summary>
    private static async Task CompleteAccountingSetupAsync(HttpClient client)
    {
        var accounts = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/accounts") ?? [];
        var byCode = accounts.ToDictionary(
            account => account.GetProperty("code").GetString()!,
            account => account.GetProperty("id").GetGuid());

        Guid Require(string code) => byCode.TryGetValue(code, out var id)
            ? id
            : throw new InvalidOperationException($"the fixture business has no account {code}");

        var settings = new
        {
            settlementScale = 2,
            cashAccountId = Require("1000"),
            bankClearingAccountId = Require("1010"),
            accountsReceivableAccountId = Require("1100"),
            inventoryAssetAccountId = Require("1200"),
            deliveredNotInvoicedAccountId = Require("1250"),
            inputTaxReceivableAccountId = Require("1200"),
            accountsPayableAccountId = Require("2101"),
            outputTaxPayableAccountId = Require("2100"),
            grniClearingAccountId = Require("2101"),
            customerDepositsAccountId = Require("2105"),
            retainedEarningsAccountId = Require("3000"),
            salesRevenueAccountId = Require("4000"),
            salesReturnsAccountId = Require("4000"),
            cogsAccountId = Require("5000"),
            purchasePriceVarianceAccountId = Require("5000"),
            inventoryShrinkageAccountId = Require("5200"),
            cashOverShortAccountId = Require("5200"),
        };

        var saved = await client.PutAsJsonAsync("/api/v1/accounting/settings", settings);
        var body = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.StatusCode == HttpStatusCode.OK, $"accounting settings could not be saved: {(int)saved.StatusCode} {body}");
    }

    private async Task PostOpeningJournalAsync(HttpClient client)
    {
        var accounts = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/accounts") ?? [];
        var cash = accounts.Single(account => account.GetProperty("code").GetString() == "1000").GetProperty("id").GetGuid();
        var revenue = accounts.Single(account => account.GetProperty("code").GetString() == "4000").GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync("/api/v1/journal-entries", new
        {
            entryDate = DateOnly.FromDateTime(DateTime.UtcNow),
            memo = "setup test posting",
            currency = "QAR",
            origin = "Manual",
            sourceId = (Guid?)null,
            lines = new object[]
            {
                new { accountId = cash, debit = 25m, credit = 0m, description = "cash in", locationId = (Guid?)null },
                new { accountId = revenue, debit = 0m, credit = 25m, description = "revenue", locationId = (Guid?)null },
            },
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"the test posting failed: {(int)response.StatusCode} {body}");
    }

    private async Task AssertRefusedAsync(HttpResponseMessage response, string subject)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(subject, body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> CountBusinessSettingsAuditAsync()
        => await _fixture.WithFreshContextAsync(db => db.AuditEvents.AsNoTracking().CountAsync(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
            && entry.EntityType == "business_settings"));

    private sealed record BusinessLocationView(Guid Id, string Name, string? Code, string? Address, bool IsActive, long Version);
}
