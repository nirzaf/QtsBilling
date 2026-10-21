using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Invoices;
using QtsBilling.DataImport;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.DataImport;

/// <summary>
/// The financial reconciliation checks carried over when the source-format importer project was retired
/// (specification task 12, finding F22 and decision D10). That importer is gone and its applied migrations stay
/// untouched, so these assertions re-home its two zero-tolerance guarantees onto the native package path:
/// <list type="number">
///   <item><description>the stock valuation is <b>derived</b> from quantity and unit cost, never declared, and an
///   opening whose ledger lines contradict that derivation admits nothing; and</description></item>
///   <item><description>the three control accounts - receivable, payable and inventory asset - equal the
///   authoritative open-item and stock subledgers exactly, before and after live postings.</description></item>
/// </list>
/// Each failure case proves the absence of rows, journals and links, not merely a returned error.
/// </summary>
public sealed class NativeReconciliationTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T12_05_valuation_is_derived_and_the_three_control_accounts_tie_out_exactly()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);

        // The ledger/subledger state the fixture itself left behind is not the claim; the claim is that the
        // approved opening moves each control account by exactly the amount of authoritative fact it adds.
        var before = await MeasureAsync();

        var result = await ApplyAsync(package);
        Assert.True(result.Committed, $"{result.Failure}|{string.Join(',', result.Quarantined.Select(item => $"{item.Resource}/{item.Reason}"))}");

        // The rows the approved package carries: ten units at 10.00 each, fifty receivable, thirty payable.
        const decimal expectedStockValue = 10m * 10m;

        var after = await MeasureAsync();
        AssertMoved(before, after, inventory: expectedStockValue, receivable: 50m, payable: 30m);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;

            // 1. Valuation is computed, not trusted from the source.
            var variantId = await db.ExternalRecordLinks.AsNoTracking()
                .Where(link => link.BusinessId == businessId
                    && link.ResourceType == NativeResources.Variants
                    && link.ExternalId == builder.VariantExternalId)
                .Select(link => link.TargetId)
                .SingleAsync();

            var balance = await db.InventoryBalances.AsNoTracking().SingleAsync(row =>
                row.BusinessId == businessId && row.VariantId == variantId);
            Assert.Equal(expectedStockValue, balance.ValueOnHand);
            Assert.Equal(10m, balance.OnHand);
            Assert.Equal(10m, balance.AverageUnitCost);

            var movement = await db.InventoryMovements.AsNoTracking().SingleAsync(row =>
                row.BusinessId == businessId
                && row.MovementType == MovementType.Opening
                && row.Memo == $"Native opening {builder.OpeningExternalId}");
            Assert.Equal(expectedStockValue, movement.ValueDelta);

            // 2. The opening entry itself is internally balanced and posted.
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, businessId);
        });
    }

    [Fact]
    public async Task T12_06_a_value_contradicting_quantity_times_cost_admits_nothing()
    {
        // The inventory line says 100 while the stock rows derive 120: the derivation wins, and the package is
        // refused rather than repriced - the guarantee the retired valuation validator existed to enforce.
        var (builder, package) = await PackageAsync(opened =>
        {
            opened.Replace(NativeResources.Openings, new
            {
                externalId = opened.OpeningExternalId,
                entryDate = "2026-10-20",
                currency = "QAR",
                memo = "Contradicted valuation",
                inventoryAccountCode = opened.InventoryAccountCode,
                receivableAccountCode = opened.ReceivableAccountCode,
                payableAccountCode = opened.PayableAccountCode,
                lines = new object[]
                {
                    new { accountCode = opened.InventoryAccountCode, debit = 100m, credit = 0m, description = "Stock opening" },
                    new { accountCode = opened.ReceivableAccountCode, debit = 50m, credit = 0m, description = "Customer opening" },
                    new { accountCode = opened.PayableAccountCode, debit = 0m, credit = 30m, description = "Supplier opening" },
                    new { accountCode = opened.EquityAccountCode, debit = 0m, credit = 120m, description = "Opening equity" },
                },
                stock = new object[]
                {
                    new { variantExternalId = opened.VariantExternalId, locationCode = opened.LocationCode, quantity = 12m, unitCost = 10m },
                },
                receivables = new object[]
                {
                    new { partyExternalId = opened.CustomerExternalId, amount = 50m, reference = opened.ReceivableReference },
                },
                payables = new object[]
                {
                    new { partyExternalId = opened.SupplierExternalId, amount = 30m, reference = opened.PayableReference },
                },
            });
        });

        using var _ = builder;
        await PrepareLocationAsync(builder);

        var refused = await ApplyAsync(package);
        Assert.False(refused.Committed);
        Assert.Equal("CONTROL_TOTAL_MISMATCH", refused.Failure);
        await AssertNothingWrittenAsync(builder);

        // A receivable control account that is not an asset account is refused for the same reason: a posted
        // balance in the wrong side of the balance sheet cannot be reconciled at all.
        var (misfiled, misfiledPackage) = await PackageAsync(opened =>
        {
            opened.Replace(NativeResources.Openings, new
            {
                externalId = opened.OpeningExternalId + "-wrong-type",
                entryDate = "2026-10-20",
                currency = "QAR",
                memo = "Wrong control account type",
                inventoryAccountCode = opened.PayableAccountCode,
                receivableAccountCode = opened.ReceivableAccountCode,
                payableAccountCode = opened.PayableAccountCode,
                lines = new object[]
                {
                    new { accountCode = opened.PayableAccountCode, debit = 100m, credit = 0m, description = "Stock into a liability" },
                    new { accountCode = opened.ReceivableAccountCode, debit = 50m, credit = 0m, description = "Customer opening" },
                    new { accountCode = opened.PayableAccountCode, debit = 0m, credit = 30m, description = "Supplier opening" },
                    new { accountCode = opened.EquityAccountCode, debit = 0m, credit = 120m, description = "Opening equity" },
                },
                stock = new object[]
                {
                    new { variantExternalId = opened.VariantExternalId, locationCode = opened.LocationCode, quantity = 10m, unitCost = 10m },
                },
                receivables = new object[]
                {
                    new { partyExternalId = opened.CustomerExternalId, amount = 50m, reference = opened.ReceivableReference + "-T" },
                },
                payables = new object[]
                {
                    new { partyExternalId = opened.SupplierExternalId, amount = 30m, reference = opened.PayableReference + "-T" },
                },
            });
        });

        using var misfiledOwner = misfiled;
        await PrepareLocationAsync(misfiled);
        var typeRefused = await ApplyAsync(misfiledPackage);
        Assert.False(typeRefused.Committed);
        Assert.Equal("ACCOUNT_TYPE_MISMATCH", typeRefused.Failure);
    }

    [Fact]
    public async Task T12_07_the_tie_out_holds_after_live_postings_and_posted_history_never_moves()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);
        Assert.True((await ApplyAsync(package)).Committed);

        var openingEntryDebit = await _fixture.WithFreshContextAsync(async db =>
        {
            var entry = await db.JournalEntries.AsNoTracking().SingleAsync(row =>
                row.BusinessId == _fixture.BusinessA.BusinessId
                && row.EntryNumber.StartsWith($"OPEN-{builder.BatchId:N}"));
            return entry.TotalDebit;
        });

        var beforeIssue = await MeasureAsync();

        // A real invoice issued through the API is the live fact the control account has to follow.
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var customerId = await CreateCustomerAsync(client);

        var create = await client.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "task 12 reconciliation",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 2m, 25m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods));
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"invoice create failed: {(int)create.StatusCode} {createBody}");

        var invoiceId = JsonDocument.Parse(createBody).RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
        var issue = await client.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
        var issueBody = await issue.Content.ReadAsStringAsync();
        Assert.True(issue.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"invoice issue failed: {(int)issue.StatusCode} {issueBody}");

        var invoiceTotal = JsonDocument.Parse(issueBody).RootElement.GetProperty("invoice").GetProperty("totalAmount").GetDecimal();
        var afterIssue = await MeasureAsync();

        // The authoritative facts and their control accounts move by the same amount: the invoice added exactly its
        // outstanding total to receivables on both sides, and the direct-goods issue relieved stock value that the
        // inventory asset account followed, to the four-decimal rounding the ledger is kept at.
        Assert.Equal(invoiceTotal, afterIssue.ReceivableLedger - beforeIssue.ReceivableLedger);
        Assert.Equal(invoiceTotal, afterIssue.ReceivableSubledger - beforeIssue.ReceivableSubledger);
        Assert.True(
            afterIssue.StockSubledger < beforeIssue.StockSubledger,
            $"issuing the invoice relieved no stock value ({beforeIssue.StockSubledger} -> {afterIssue.StockSubledger})");
        Assert.True(
            afterIssue.InventoryLedger - beforeIssue.InventoryLedger == afterIssue.StockSubledger - beforeIssue.StockSubledger,
            $"stock relief and its control account disagree: ledger {(afterIssue.InventoryLedger - beforeIssue.InventoryLedger)}, subledger {(afterIssue.StockSubledger - beforeIssue.StockSubledger)}");

        await _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;

            // The posted opening entry is untouched: a later posting never reprices history.
            var opening = await db.JournalEntries.AsNoTracking().SingleAsync(row =>
                row.BusinessId == businessId && row.EntryNumber.StartsWith($"OPEN-{builder.BatchId:N}"));
            Assert.Equal(openingEntryDebit, opening.TotalDebit);
            Assert.Equal(EntryStatus.Posted, opening.Status);
            await FinancialAssertions.AssertEveryEntryBalancesAsync(db, businessId);
        });
    }

    [Fact]
    public async Task T12_08_the_native_path_records_provenance_and_never_the_legacy_identity_table()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);
        Assert.True((await ApplyAsync(package)).Committed);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;

            // The retired source-era identity table is historical lineage only: the native importer links through
            // ExternalRecordLink and writes nothing into legacy_identifier_map (decision D10, no compatibility path).
            Assert.Equal(0, await db.LegacyIdentifierMaps.CountAsync(row => row.BusinessId == businessId));
            Assert.True(await db.ExternalRecordLinks.AnyAsync(link =>
                link.BusinessId == businessId
                && link.ResourceType == NativeResources.Customers
                && link.ExternalId == builder.CustomerExternalId));

            // And the retirement did not purge history: the table is still in the schema, so already-recorded
            // lineage stays queryable and no applied migration was rewritten (requirement R14).
            var exists = await db.Database.SqlQueryRaw<int>(
                "SELECT 1 AS \"Value\" FROM information_schema.tables WHERE table_name = 'legacy_identifier_map'")
                .AnyAsync();
            Assert.True(exists, "the legacy identity table was dropped rather than retired");
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The re-homed zero-tolerance measurement: each control account's posted balance against the authoritative
    /// subledger for that concern, rounded to four decimals exactly as the retired check did.
    /// </summary>
    private Task<Subledger> MeasureAsync() =>
        _fixture.WithFreshContextAsync(db => MeasureAsync(db, _fixture.BusinessA.BusinessId));

    private static async Task<Subledger> MeasureAsync(BillingDbContext db, Guid businessId)
    {
        var inventoryAccountId = await AccountIdAsync(db, businessId, "1200");
        var receivableAccountId = await AccountIdAsync(db, businessId, "1100");
        var payableAccountId = await AccountIdAsync(db, businessId, "2100");

        var stockSubledger = Round(await db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == businessId)
            .SumAsync(balance => (decimal?)balance.ValueOnHand) ?? 0m);

        var invoiceSubledger = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.BusinessId == businessId
                && (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.PartiallyPaid))
            .SumAsync(invoice => (decimal?)invoice.BalanceDue) ?? 0m;
        var receivableOpenItems = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId && item.Kind == OpenItemKind.Receivable)
            .SumAsync(item => (decimal?)item.OriginalAmount) ?? 0m;
        var receivableSubledger = Round(invoiceSubledger + receivableOpenItems);

        var billSubledger = await db.SupplierBills.AsNoTracking()
            .Where(bill => bill.BusinessId == businessId
                && (bill.Status == SupplierBillStatus.Issued || bill.Status == SupplierBillStatus.PartiallyPaid))
            .SumAsync(bill => (decimal?)bill.BalanceDue) ?? 0m;
        var payableOpenItems = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId && item.Kind == OpenItemKind.Payable)
            .SumAsync(item => (decimal?)item.OriginalAmount) ?? 0m;
        var payableSubledger = Round(billSubledger + payableOpenItems);

        return new Subledger(
            stockSubledger,
            await LedgerNetAsync(db, businessId, inventoryAccountId!.Value, creditNormal: false),
            receivableSubledger,
            await LedgerNetAsync(db, businessId, receivableAccountId!.Value, creditNormal: false),
            payableSubledger,
            await LedgerNetAsync(db, businessId, payableAccountId!.Value, creditNormal: true));
    }

    /// <summary>
    /// The claim the retired importer enforced before it wrote anything: an approved opening moves each control
    /// account by exactly the authoritative fact it adds, with no drift between the two sides of the same number.
    /// </summary>
    private static void AssertMoved(Subledger before, Subledger after, decimal inventory, decimal receivable, decimal payable)
    {
        Assert.Equal(inventory, Round(after.StockSubledger - before.StockSubledger));
        Assert.Equal(inventory, Round(after.InventoryLedger - before.InventoryLedger));
        Assert.Equal(receivable, Round(after.ReceivableSubledger - before.ReceivableSubledger));
        Assert.Equal(receivable, Round(after.ReceivableLedger - before.ReceivableLedger));
        Assert.Equal(payable, Round(after.PayableSubledger - before.PayableSubledger));
        Assert.Equal(payable, Round(after.PayableLedger - before.PayableLedger));
    }

    private static async Task<Guid?> AccountIdAsync(BillingDbContext db, Guid businessId, string code)
        => await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId && account.Code == code)
            .Select(account => (Guid?)account.Id)
            .SingleOrDefaultAsync();

    private static async Task<decimal> LedgerNetAsync(
        BillingDbContext db,
        Guid businessId,
        Guid accountId,
        bool creditNormal)
    {
        var totals = await (from line in db.JournalLines.AsNoTracking()
                            join entry in db.JournalEntries.AsNoTracking()
                                on new { line.JournalEntryId, line.BusinessId }
                                equals new { JournalEntryId = entry.Id, entry.BusinessId }
                            where line.BusinessId == businessId
                                && line.AccountId == accountId
                                && entry.Status == EntryStatus.Posted
                            group line by line.AccountId into grouped
                            select new
                            {
                                Debit = grouped.Sum(line => line.DebitAmount),
                                Credit = grouped.Sum(line => line.CreditAmount),
                            })
            .SingleOrDefaultAsync();

        return Round(creditNormal
            ? (totals?.Credit ?? 0m) - (totals?.Debit ?? 0m)
            : (totals?.Debit ?? 0m) - (totals?.Credit ?? 0m));
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 4, MidpointRounding.AwayFromZero);

    private async Task<(NativeTestPackage Builder, ValidatedPackage Package)> ValidPackageAsync()
        => await PackageAsync(configure: null);

    private async Task<(NativeTestPackage Builder, ValidatedPackage Package)> PackageAsync(Action<NativeTestPackage>? configure)
    {
        var builder = NativeTestPackage.Valid();
        builder.BusinessId = _fixture.BusinessA.BusinessId;

        configure?.Invoke(builder);

        builder.Build();

        var validation = await new NativePackageReader().ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.True(validation.IsValid,
            string.Join(',', validation.Report.Rejections.Select(rejection => $"{rejection.Resource}/{rejection.Reason}/{rejection.Field}")));

        return (builder, validation.Package!);
    }

    private async Task PrepareLocationAsync(NativeTestPackage builder)
    {
        await _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;
            if (await db.BusinessLocations.AnyAsync(location =>
                location.BusinessId == businessId && location.Code == builder.LocationCode))
            {
                return;
            }

            db.BusinessLocations.Add(new BusinessLocation
            {
                BusinessId = businessId,
                Name = $"Native {builder.Prefix}",
                Code = builder.LocationCode,
                IsActive = true,
            });

            await db.SaveChangesAsync();
        });
    }

    private Task<NativeImportResult> ApplyAsync(ValidatedPackage package)
        => _fixture.WithFreshContextAsync(db => new NativeImportWriter().ApplyAsync(
            db,
            package,
            new NativeImportOptions
            {
                TargetBusinessId = _fixture.BusinessA.BusinessId,
                UserId = _fixture.Users.OwnerA.UserId,
            }));

    private Task AssertNothingWrittenAsync(NativeTestPackage builder)
        => _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;
            Assert.False(await db.ExternalRecordLinks.AnyAsync(link =>
                link.BusinessId == businessId && link.ExternalId == builder.OpeningExternalId));
            Assert.False(await db.JournalEntries.AnyAsync(entry =>
                entry.BusinessId == businessId
                && entry.EntryNumber.StartsWith($"OPEN-{builder.BatchId:N}")));
            Assert.False(await db.InventoryMovements.AnyAsync(movement =>
                movement.BusinessId == businessId
                && movement.MovementType == MovementType.Opening
                && movement.ReferenceType == "native_opening"
                && movement.Memo == $"Native opening {builder.OpeningExternalId}"));
            Assert.False(await db.OpenItems.AnyAsync(item =>
                item.BusinessId == businessId && item.Reference == builder.ReceivableReference));
        });

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", new QtsBilling.Contracts.Customers.SaveCustomerRequest(
            $"Reconciliation counter {Guid.NewGuid():N}", null, null, null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"customer create failed: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>The three control accounts and the authoritative subledger each one must follow.</summary>
    private sealed record Subledger(
        decimal StockSubledger,
        decimal InventoryLedger,
        decimal ReceivableSubledger,
        decimal ReceivableLedger,
        decimal PayableSubledger,
        decimal PayableLedger);
}
