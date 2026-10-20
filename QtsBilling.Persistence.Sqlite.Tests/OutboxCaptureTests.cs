using QtsBilling.Persistence.Sqlite.Entities;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class OutboxCaptureTests
{
    [Fact]
    public async Task Capture_writes_draft_lines_cash_and_outbox_in_one_transaction()
    {
        await using var harness = LocalStoreHarness.Create();
        var startup = await harness.Store.InitializeAsync();

        Assert.Equal(5, startup.AppliedMigrations.Count);
        Assert.Equal(5, startup.SchemaVersion);
        Assert.Equal("wal", startup.JournalMode);
        Assert.True(startup.IntegrityCheckPassed);
        Assert.Null(startup.PreMigrationBackupPath);

        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(2L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_lines;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));

        Assert.Equal(123400L, harness.ScalarLong("SELECT amount_scaled FROM local_sale_drafts;"));
        Assert.Equal(123400L, harness.ScalarLong("SELECT amount_scaled FROM local_cash_movements;"));
        Assert.Equal(2500000L, harness.ScalarLong("SELECT qty_scaled FROM local_sale_lines WHERE ordinal = 0;"));
        Assert.Equal(40000L, harness.ScalarLong("SELECT unit_price_scaled FROM local_sale_lines WHERE ordinal = 0;"));
        Assert.Equal("cash", harness.ScalarString("SELECT payment_method FROM local_sale_drafts;"));
        Assert.Equal("Pending", harness.ScalarString("SELECT state FROM outbox_operations;"));
        Assert.Equal(operationId.ToString("D"), harness.ScalarString("SELECT operation_id FROM local_sale_drafts;"), ignoreCase: true);

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.Single(due);
        Assert.Equal(operationId, due[0].OperationId);
        Assert.Equal(0, due[0].AttemptCount);

        var summary = await harness.Store.GetPendingSummaryAsync();
        Assert.Equal(1, summary.PendingOperations);
        Assert.Equal(0, summary.RejectedOperations);
        Assert.Equal(12.34m, summary.ProvisionalCashAtRisk);
    }

    [Fact]
    public async Task Replaying_the_same_operation_is_a_no_op_and_a_changed_payload_is_rejected()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operationId = Guid.CreateVersion7();
        var sale = TestData.Sale(operationId);
        await harness.Store.CaptureProvisionalCashSaleAsync(sale);
        await harness.Store.CaptureProvisionalCashSaleAsync(sale);

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));

        var tampered = sale with { CanonicalPayload = "{\"type\":\"pos.sale.capture.v1\",\"sale\":999}" };
        await Assert.ThrowsAsync<SqliteSameKeyDifferentPayloadException>(
            () => harness.Store.CaptureProvisionalCashSaleAsync(tampered));

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
    }

    [Fact]
    public async Task Unrepresentable_precision_rolls_back_the_whole_capture()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var sale = TestData.Sale() with
        {
            Lines =
            [
                new ProvisionalSaleLineInput(
                    Guid.CreateVersion7(),
                    Quantity: 0.0000001m,
                    UnitPrice: 4m,
                    Tax: 0m,
                    Discount: 0m,
                    ExpectedCatalogRevision: 1),
            ],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Store.CaptureProvisionalCashSaleAsync(sale));

        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_lines;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
    }

    [Fact]
    public async Task Overflowing_money_rolls_back_the_whole_capture()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var sale = TestData.Sale(amount: decimal.MaxValue);

        await Assert.ThrowsAsync<OverflowException>(() => harness.Store.CaptureProvisionalCashSaleAsync(sale));

        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
    }

    [Fact]
    public async Task Pending_outbox_and_money_survive_a_reopen_and_a_cache_rebuild()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        await harness.Store.ReplaceCachedProductsAsync(
            businessId,
            locationId,
            [
                new CachedProduct
                {
                    BusinessId = businessId,
                    LocationId = locationId,
                    ProductId = Guid.CreateVersion7(),
                    Sku = "A",
                    Name = "Cache row",
                    Price = 9.99m,
                    TaxRate = 0.15m,
                    Active = true,
                    Revision = 1,
                },
            ]);
        Assert.Equal(1, await harness.Store.GetCachedProductCountAsync());

        var rebuild = await harness.Store.RebuildCacheAsync();
        Assert.Equal(1, rebuild.Products);
        Assert.Equal(0, await harness.Store.GetCachedProductCountAsync());

        harness.Reopen();
        var startup = await harness.Store.InitializeAsync();
        Assert.Empty(startup.AppliedMigrations);

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(2L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_lines;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM cached_products;"));

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.Single(due);
        Assert.Equal(operationId, due[0].OperationId);
    }

    [Fact]
    public async Task In_flight_operations_are_recovered_to_pending_on_startup()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operation = TestData.Operation();
        await harness.Store.EnqueueOutboxOperationAsync(operation);
        await harness.Store.MarkOutboxSendingAsync(operation.OperationId);

        Assert.Equal("Sending", harness.ScalarString("SELECT state FROM outbox_operations;"));

        harness.Reopen();
        var startup = await harness.Store.InitializeAsync();

        Assert.Equal(1, startup.RecoveredSendingOperations);
        Assert.Equal("Pending", harness.ScalarString("SELECT state FROM outbox_operations;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT attempt_count FROM outbox_operations;"));

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.Single(due);
    }

    [Fact]
    public async Task Check_constraints_and_foreign_keys_protect_pending_money_records()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        Assert.ThrowsAny<Exception>(
            () => harness.Execute("UPDATE local_sale_drafts SET payment_method = 'card';"));
        Assert.ThrowsAny<Exception>(
            () => harness.Execute("UPDATE outbox_operations SET state = 'Bogus';"));

        // A pending money row must not silently disappear with its outbox row (RESTRICT, not CASCADE).
        Assert.ThrowsAny<Exception>(
            () => harness.Execute("DELETE FROM outbox_operations;", foreignKeys: true));

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
    }
}
