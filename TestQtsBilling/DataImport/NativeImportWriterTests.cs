using Microsoft.EntityFrameworkCore;
using QtsBilling.DataImport;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.DataImport;

/// <summary>
/// Task 19 against real PostgreSQL: one transactional native import, immutable source identity, stable
/// reruns and opening control totals. Every failure assertion proves the absence of the target rows and
/// links, not merely that an exception was returned.
/// </summary>
public sealed class NativeImportWriterTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public NativeImportWriterTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T19_01_interrupted_import_leaves_no_domain_rows_or_links()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);

        var result = await ApplyAsync(
            package,
            new NativeImportOptions
            {
                TargetBusinessId = _fixture.BusinessA.BusinessId,
                UserId = _fixture.Users.OwnerA.UserId,
                BeforeCommit = _ => throw new InvalidOperationException("injected interruption"),
            });

        Assert.False(result.Committed);
        Assert.NotEmpty(result.Quarantined);
        await AssertNoImportAsync(builder);
    }

    [Fact]
    public async Task T19_02_rerun_reuses_targets_and_leaves_journal_and_stock_unchanged()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);

        var first = await ApplyAsync(package);
        if (!first.Committed)
        {
            throw new InvalidOperationException(
                $"{first.Failure}|{string.Join(",", first.Quarantined.Select(item => $"{item.Resource}/{item.Reason}/{item.Field}"))}");
        }
        Assert.Single(first.Openings);

        var before = await SnapshotAsync(builder, first.Openings[0].JournalEntryId);
        var second = await ApplyAsync(package);
        if (!second.Committed)
        {
            throw new InvalidOperationException(
                $"{second.Failure}|{string.Join(",", second.Quarantined.Select(item => $"{item.Resource}/{item.Reason}/{item.Field}"))}");
        }
        Assert.Equal(0, second.ImportedRecords);
        Assert.True(second.ReusedRecords > 0);
        var after = await SnapshotAsync(builder, first.Openings[0].JournalEntryId);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task T19_03_foreign_business_changed_body_and_native_key_are_refused()
    {
        var (foreignBuilder, foreignPackage) = await ValidPackageAsync();
        using var foreignOwner = foreignBuilder;

        var foreign = await ApplyAsync(
            foreignPackage,
            new NativeImportOptions
            {
                TargetBusinessId = _fixture.BusinessB.BusinessId,
                UserId = _fixture.Users.OwnerB.UserId,
            });
        Assert.False(foreign.Committed);
        Assert.Equal("BUSINESS_MISMATCH", foreign.Failure);
        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.False(await db.ExternalRecordLinks.AnyAsync(link =>
                link.BusinessId == _fixture.BusinessB.BusinessId
                && link.ExternalId == foreignBuilder.CustomerExternalId));
        });

        var (changedBuilder, changedPackage) = await ValidPackageAsync();
        using var changedOwner = changedBuilder;
        await PrepareLocationAsync(changedBuilder);
        Assert.True((await ApplyAsync(changedPackage)).Committed);

        changedBuilder.Replace(NativeResources.Customers, new
        {
            externalId = changedBuilder.CustomerExternalId,
            name = $"Changed after approval {changedBuilder.Prefix}",
            isActive = true,
        });
        var changedValidation = await new NativePackageReader().ValidateAsync(
            changedBuilder.Root,
            NativeTestData.Trust());
        Assert.True(changedValidation.IsValid);
        var changed = await ApplyAsync(changedValidation.Package!);
        Assert.False(changed.Committed);
        Assert.Contains(changed.Quarantined, rejection => rejection.Reason == "LINK_CONFLICT");

        var (collisionBuilder, collisionPackage) = await ValidPackageAsync();
        using var collisionOwner = collisionBuilder;
        var normalizedPhone = NativePackageReader.NormalizePhone(collisionBuilder.CustomerPhone);
        await _fixture.WithFreshContextAsync(async db =>
        {
            db.Customers.Add(new Customer
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                Name = $"Existing {collisionBuilder.Prefix}",
                Phone = collisionBuilder.CustomerPhone,
                PhoneNormalized = normalizedPhone,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        });

        var collision = await ApplyAsync(collisionPackage);
        Assert.False(collision.Committed);
        Assert.Contains(collision.Quarantined, rejection => rejection.Reason == "NATURAL_KEY_CONFLICT");
        await AssertNoImportAsync(collisionBuilder);
    }

    [Fact]
    public async Task T19_04_openings_equal_general_ledger_control_and_stock_detail()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);

        var result = await ApplyAsync(package);
        Assert.True(result.Committed);
        var opening = Assert.Single(result.Openings);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var entry = await db.JournalEntries.SingleAsync(candidate => candidate.Id == opening.JournalEntryId);
            Assert.Equal(EntryStatus.Posted, entry.Status);
            Assert.Equal(JournalSourceType.MigrationOpening, entry.SourceType);
            Assert.Equal(150m, entry.TotalDebit);
            Assert.Equal(150m, entry.TotalCredit);

            var lines = await db.JournalLines
                .Where(line => line.JournalEntryId == entry.Id)
                .ToListAsync();
            var accounts = await db.Accounts
                .Where(account => account.BusinessId == _fixture.BusinessA.BusinessId)
                .ToDictionaryAsync(account => account.Code);
            var inventoryNet = lines
                .Where(line => line.AccountId == accounts["1200"].Id)
                .Sum(line => line.DebitAmount - line.CreditAmount);
            var receivableNet = lines
                .Where(line => line.AccountId == accounts["1100"].Id)
                .Sum(line => line.DebitAmount - line.CreditAmount);
            var payableNet = lines
                .Where(line => line.AccountId == accounts["2100"].Id)
                .Sum(line => line.CreditAmount - line.DebitAmount);

            Assert.Equal(opening.InventoryValue, inventoryNet);
            Assert.Equal(opening.ReceivableTotal, receivableNet);
            Assert.Equal(opening.PayableTotal, payableNet);

            var openItems = await db.OpenItems
                .Where(item => item.JournalEntryId == entry.Id)
                .ToListAsync();
            Assert.Equal(2, openItems.Count);
            Assert.Equal(
                opening.ReceivableTotal,
                openItems.Where(item => item.Kind == OpenItemKind.Receivable).Sum(item => item.OriginalAmount));
            Assert.Equal(
                opening.PayableTotal,
                openItems.Where(item => item.Kind == OpenItemKind.Payable).Sum(item => item.OriginalAmount));

            var variantId = await db.ExternalRecordLinks
                .Where(link => link.BusinessId == _fixture.BusinessA.BusinessId
                    && link.ResourceType == NativeResources.Variants
                    && link.ExternalId == builder.VariantExternalId)
                .Select(link => link.TargetId)
                .SingleAsync();
            var movement = await db.InventoryMovements.SingleAsync(candidate =>
                candidate.BusinessId == _fixture.BusinessA.BusinessId
                && candidate.VariantId == variantId
                && candidate.MovementType == MovementType.Opening);
            Assert.Equal(10m, movement.QuantityDelta);
            Assert.Equal(100m, movement.ValueDelta);

            var balance = await db.InventoryBalances.SingleAsync(candidate =>
                candidate.BusinessId == _fixture.BusinessA.BusinessId
                && candidate.VariantId == variantId);
            Assert.Equal(10m, balance.OnHand);
            Assert.Equal(100m, balance.ValueOnHand);
        });
    }

    [Fact]
    public async Task T19_05_database_rejection_midway_leaves_no_partial_rows()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);

        var result = await ApplyAsync(
            package,
            new NativeImportOptions
            {
                TargetBusinessId = _fixture.BusinessA.BusinessId,
                UserId = Guid.NewGuid(),
            });

        Assert.False(result.Committed);
        Assert.NotEmpty(result.Quarantined);
        await AssertNoImportAsync(builder);
    }

    [Fact]
    public async Task T19_06_control_total_mismatch_is_refused_before_any_write_commits()
    {
        var (builder, package) = await ValidPackageAsync();
        using var _ = builder;
        await PrepareLocationAsync(builder);

        builder.Replace(NativeResources.Openings, new NativeOpeningRow
        {
            ExternalId = builder.OpeningExternalId,
            EntryDate = "2026-10-20",
            Currency = "QAR",
            Memo = "Controls mismatch",
            InventoryAccountCode = "1200",
            ReceivableAccountCode = "1100",
            PayableAccountCode = "2100",
            Lines =
            [
                new NativeOpeningLineRow { AccountCode = "1200", Debit = 99m },
                new NativeOpeningLineRow { AccountCode = "1100", Debit = 50m },
                new NativeOpeningLineRow { AccountCode = "2100", Credit = 30m },
                new NativeOpeningLineRow { AccountCode = "4000", Credit = 119m },
            ],
            Stock =
            [
                new NativeStockOpeningRow
                {
                    VariantExternalId = builder.VariantExternalId,
                    LocationCode = builder.LocationCode,
                    Quantity = 10m,
                    UnitCost = 10m,
                },
            ],
            Receivables =
            [
                new NativePartyOpeningRow
                {
                    PartyExternalId = builder.CustomerExternalId,
                    Amount = 50m,
                    Reference = builder.ReceivableReference,
                },
            ],
            Payables =
            [
                new NativePartyOpeningRow
                {
                    PartyExternalId = builder.SupplierExternalId,
                    Amount = 30m,
                    Reference = builder.PayableReference,
                },
            ],
        });

        var validation = await new NativePackageReader().ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.True(validation.IsValid);
        var result = await ApplyAsync(validation.Package!);
        Assert.False(result.Committed);
        Assert.Contains(result.Quarantined, rejection => rejection.Reason == "CONTROL_TOTAL_MISMATCH");
        await AssertNoImportAsync(builder);
    }

    private async Task<(NativeTestPackage Builder, ValidatedPackage Package)> ValidPackageAsync()
    {
        var builder = NativeTestPackage.Valid();
        builder.BusinessId = _fixture.BusinessA.BusinessId;
        builder.Build();
        var validation = await new NativePackageReader().ValidateAsync(builder.Root, NativeTestData.Trust());
        Assert.True(validation.IsValid);
        return (builder, validation.Package!);
    }

    private async Task PrepareLocationAsync(NativeTestPackage builder)
    {
        await _fixture.WithFreshContextAsync(async db =>
        {
            var exists = await db.BusinessLocations.AnyAsync(location =>
                location.BusinessId == _fixture.BusinessA.BusinessId
                && location.Code == builder.LocationCode);

            if (!exists)
            {
                db.BusinessLocations.Add(new BusinessLocation
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    Name = $"Native {builder.Prefix}",
                    Code = builder.LocationCode,
                    IsActive = true,
                });
                await db.SaveChangesAsync();
            }
        });
    }

    private Task<NativeImportResult> ApplyAsync(
        ValidatedPackage package,
        NativeImportOptions? options = null) =>
        _fixture.WithFreshContextAsync(db => new NativeImportWriter().ApplyAsync(
            db,
            package,
            options ?? new NativeImportOptions
            {
                TargetBusinessId = _fixture.BusinessA.BusinessId,
                UserId = _fixture.Users.OwnerA.UserId,
            }));

    private Task AssertNoImportAsync(NativeTestPackage builder) =>
        _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;
            Assert.False(await db.ExternalRecordLinks.AnyAsync(link =>
                link.BusinessId == businessId && link.ExternalId == builder.CustomerExternalId));
            Assert.False(await db.Customers.AnyAsync(customer =>
                customer.BusinessId == businessId && customer.Name == $"Acme Retail {builder.Prefix}"));
            Assert.False(await db.JournalEntries.AnyAsync(entry =>
                entry.BusinessId == businessId
                && entry.EntryNumber.StartsWith($"OPEN-{builder.BatchId:N}")));
            Assert.False(await db.InventoryMovements.AnyAsync(movement =>
                movement.BusinessId == businessId
                && movement.ReferenceType == "native_opening"
                && movement.Memo == $"Native opening {builder.OpeningExternalId}"));
            Assert.False(await db.OpenItems.AnyAsync(item =>
                item.BusinessId == businessId && item.Reference == builder.ReceivableReference));
        });

    private Task<ImportSnapshot> SnapshotAsync(NativeTestPackage builder, Guid journalEntryId) =>
        _fixture.WithFreshContextAsync(async db =>
        {
            var businessId = _fixture.BusinessA.BusinessId;
            var customerId = await db.ExternalRecordLinks
                .Where(link => link.BusinessId == businessId
                    && link.ResourceType == NativeResources.Customers
                    && link.ExternalId == builder.CustomerExternalId)
                .Select(link => link.TargetId)
                .SingleAsync();
            var categoryId = await db.ExternalRecordLinks
                .Where(link => link.BusinessId == businessId
                    && link.ResourceType == NativeResources.Categories
                    && link.ExternalId == builder.CategoryExternalId)
                .Select(link => link.TargetId)
                .SingleAsync();
            var productId = await db.ExternalRecordLinks
                .Where(link => link.BusinessId == businessId
                    && link.ResourceType == NativeResources.Products
                    && link.ExternalId == builder.ProductExternalId)
                .Select(link => link.TargetId)
                .SingleAsync();
            var variantId = await db.ExternalRecordLinks
                .Where(link => link.BusinessId == businessId
                    && link.ResourceType == NativeResources.Variants
                    && link.ExternalId == builder.VariantExternalId)
                .Select(link => link.TargetId)
                .SingleAsync();
            var customer = await db.Customers.SingleAsync(entity => entity.Id == customerId);
            var entry = await db.JournalEntries.SingleAsync(entity => entity.Id == journalEntryId);
            var movement = await db.InventoryMovements.SingleAsync(entity =>
                entity.BusinessId == businessId
                && entity.VariantId == variantId
                && entity.MovementType == MovementType.Opening);
            var balance = await db.InventoryBalances.SingleAsync(entity =>
                entity.BusinessId == businessId && entity.VariantId == variantId);
            var openItemCount = await db.OpenItems.CountAsync(item => item.JournalEntryId == journalEntryId);

            return new ImportSnapshot(
                customerId,
                customer.Version,
                categoryId,
                productId,
                variantId,
                journalEntryId,
                entry.Status,
                entry.TotalDebit,
                entry.TotalCredit,
                movement.Id,
                movement.QuantityDelta,
                movement.ValueDelta,
                balance.Id,
                balance.OnHand,
                balance.ValueOnHand,
                openItemCount);
        });

    private sealed record ImportSnapshot(
        Guid CustomerId,
        long CustomerVersion,
        Guid CategoryId,
        Guid ProductId,
        Guid VariantId,
        Guid JournalEntryId,
        EntryStatus Status,
        decimal TotalDebit,
        decimal TotalCredit,
        Guid MovementId,
        decimal MovementQuantity,
        decimal MovementValue,
        Guid BalanceId,
        decimal OnHand,
        decimal ValueOnHand,
        int OpenItemCount);
}
