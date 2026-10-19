using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Sqlite.Entities;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class CacheAndSchemaTests
{
    [Fact]
    public async Task Price_queries_use_scaled_integer_comparisons()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        await harness.Store.ReplaceCachedProductsAsync(
            businessId,
            locationId,
            [
                Product(businessId, locationId, "NINE", "Nine", 9.99m),
                Product(businessId, locationId, "TEN", "Ten", 10.00m),
                Product(businessId, locationId, "HUNDRED", "Hundred", 100.00m),
            ]);

        var above = await harness.Store.GetCachedProductsAbovePriceAsync(9.99m);
        Assert.Equal(["Ten", "Hundred"], above.Select(product => product.Name).ToArray());

        Assert.Equal("integer", harness.ScalarString("SELECT typeof(price_scaled) FROM cached_products LIMIT 1;"));
        Assert.Equal(99900L, harness.ScalarLong("SELECT price_scaled FROM cached_products WHERE sku = 'NINE';"));
        Assert.Equal(100000L, harness.ScalarLong("SELECT price_scaled FROM cached_products WHERE sku = 'TEN';"));
        Assert.Equal(150000L, harness.ScalarLong("SELECT tax_scaled FROM cached_products WHERE sku = 'NINE';"));

        var counted = await harness.Store.GetCachedProductsAbovePriceAsync(9.98m);
        Assert.Equal(3, counted.Count);
    }

    [Fact]
    public async Task Cache_replacement_round_trips_customers_stock_and_tombstones()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();

        await harness.Store.ReplaceCachedCustomersAsync(
            businessId,
            [
                new CachedCustomer
                {
                    BusinessId = businessId,
                    CustomerId = Guid.CreateVersion7(),
                    FieldsNeededOffline = "{\"name\":\"Walk-in\"}",
                    Revision = 3,
                },
            ]);
        await harness.Store.ReplaceCachedStockAsync(
            businessId,
            locationId,
            [
                new CachedStock
                {
                    BusinessId = businessId,
                    LocationId = locationId,
                    VariantId = Guid.CreateVersion7(),
                    ServerQuantity = 12.5m,
                    Revision = 4,
                },
            ]);
        await harness.Store.ReplaceCacheTombstonesAsync(
            [new CacheTombstone { EntityType = "product", Id = Guid.CreateVersion7(), DeletedRevision = 5 }]);

        Assert.Equal(1, await harness.Store.GetCachedCustomerCountAsync());
        Assert.Equal(1, await harness.Store.GetCachedStockCountAsync());
        Assert.Equal(12500000L, harness.ScalarLong("SELECT server_qty_scaled FROM cached_stock;"));

        var rebuild = await harness.Store.RebuildCacheAsync();
        Assert.Equal(1, rebuild.Customers);
        Assert.Equal(1, rebuild.Stock);
        Assert.Equal(1, rebuild.Tombstones);
        Assert.Equal(0, await harness.Store.GetCachedCustomerCountAsync());
        Assert.Equal(0, await harness.Store.GetCachedStockCountAsync());
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM cache_tombstones;"));
    }

    [Fact]
    public async Task Unknown_future_migration_blocks_startup_and_preserves_money_records()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        harness.Close();
        harness.Execute(
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) " +
            "VALUES ('20990101000000_FutureMigration', '10.0.13');");
        harness.Open();

        var exception = await Assert.ThrowsAsync<SqliteSchemaCompatibilityException>(
            () => harness.Store.InitializeAsync());

        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(2L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_lines;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(operationId.ToString("D"), harness.ScalarString("SELECT operation_id FROM local_sale_drafts;"), ignoreCase: true);
        Assert.Equal(123400L, harness.ScalarLong("SELECT amount_scaled FROM local_sale_drafts;"));
    }

    [Fact]
    public async Task Newer_database_user_version_blocks_startup_and_preserves_money_records()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale());

        harness.Close();
        harness.Execute("PRAGMA user_version = 99;");
        harness.Open();

        var exception = await Assert.ThrowsAsync<SqliteSchemaCompatibilityException>(
            () => harness.Store.InitializeAsync());

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
    }

    [Fact]
    public async Task Upgrading_schema_with_pending_records_keeps_them_and_snapshots_a_backup()
    {
        await using var harness = LocalStoreHarness.Create();

        string firstMigration;
        string secondMigration;
        string thirdMigration;
        await using (var db = harness.CreateContext())
        {
            var known = db.Database.GetMigrations().ToArray();
            Assert.Equal(3, known.Length);
            firstMigration = known[0];
            secondMigration = known[1];
            thirdMigration = known[2];
            await db.Database.MigrateAsync(firstMigration);
        }

        var operationId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();
        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var registerId = Guid.CreateVersion7();
        var variantA = Guid.CreateVersion7();
        var variantB = Guid.CreateVersion7();
        var payload = "{\"type\":\"pos.sale.capture.v1\",\"seeded\":true}";
        var fingerprint = SqlitePayloadHash.Compute(payload);

        harness.Execute($"""
            INSERT INTO outbox_operations
                (operation_id, device_id, business_id, location_id, operation_type, payload_version,
                 canonical_payload, sha256_fingerprint, state, attempts, next_attempt_utc, created_at_utc)
            VALUES
                ('{operationId:D}', '{deviceId:D}', '{businessId:D}', '{locationId:D}', 'pos.sale.capture.v1', 1,
                 '{payload}', '{fingerprint}', 'Pending', 0,
                 '2020-01-01 00:00:00.0000000', '2020-01-01 00:00:00.0000000');

            INSERT INTO local_sale_drafts
                (operation_id, provisional_reference, user_id, register_id, lines_json, payment_method,
                 amount_scaled, state, last_error, created_at_utc, updated_at_utc)
            VALUES
                ('{operationId:D}', 'PROV-SEEDED', '{userId:D}', '{registerId:D}', NULL, 'cash',
                 123400, 'Pending', NULL,
                 '2020-01-01 00:00:00.0000000', '2020-01-01 00:00:00.0000000');

            INSERT INTO local_sale_lines
                (operation_id, ordinal, variant_id, qty_scaled, unit_price_scaled, tax_scaled,
                 discount_scaled, expected_catalog_revision)
            VALUES
                ('{operationId:D}', 0, '{variantA:D}', 2500000, 40000, 10000, 5000, 7),
                ('{operationId:D}', 1, '{variantB:D}', 1000000, 53400, 0, 0, 7);

            INSERT INTO local_cash_movements
                (operation_id, register_id, amount_scaled, reason, state)
            VALUES
                ('{operationId:D}', '{registerId:D}', 123400, 'provisional cash sale', 'Pending');
            """);

        harness.Close();
        harness.Open();

        var startup = await harness.Store.InitializeAsync();

        Assert.Equal([secondMigration, thirdMigration], startup.AppliedMigrations);
        Assert.NotNull(startup.PreMigrationBackupPath);
        Assert.True(File.Exists(startup.PreMigrationBackupPath!));

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(2L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_lines;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(123400L, harness.ScalarLong("SELECT amount_scaled FROM local_sale_drafts;"));
        Assert.Equal(2500000L, harness.ScalarLong("SELECT qty_scaled FROM local_sale_lines WHERE ordinal = 0;"));
        Assert.Equal("PROV-SEEDED", harness.ScalarString("SELECT provisional_reference FROM local_sale_drafts;"));

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddDays(1));
        Assert.Single(due);
        Assert.Equal(operationId, due[0].OperationId);

        var schema = harness.ScalarString(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'local_sale_lines';");
        Assert.Contains("ck_local_sale_lines_discount_non_negative", schema, StringComparison.Ordinal);
        Assert.Contains("ck_local_sale_lines_unit_price_non_negative", schema, StringComparison.Ordinal);

        // The pre-migration snapshot is a real, self-contained database with the pending money row.
        Assert.Equal(1L, LocalStoreHarness.ScalarLongIn(
            startup.PreMigrationBackupPath!, "SELECT COUNT(*) FROM local_sale_drafts;"));
    }

    private static CachedProduct Product(Guid businessId, Guid locationId, string sku, string name, decimal price)
        => new()
        {
            BusinessId = businessId,
            LocationId = locationId,
            ProductId = Guid.CreateVersion7(),
            Sku = sku,
            Name = name,
            Price = price,
            TaxRate = 0.15m,
            Active = true,
            Revision = 1,
        };
}
