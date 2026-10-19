using QtsBilling.Contracts.Pos;
using QtsBilling.Persistence.Sqlite.Entities;
using QtsBilling.Persistence.Sqlite.Pos;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class LocalPosStoreTests
{
    private static LocalDeviceContext Context() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7());

    private static LocalCashSaleDraft Draft(Guid operationId, decimal cash = 12.34m) => new(
        operationId,
        cash,
        [
            new LocalSaleLineDraft(0, Guid.CreateVersion7(), 2m, 5.00m, 0.068m, 0.34m, 0m, 7),
        ]);

    [Fact]
    public async Task Capture_stores_the_money_rows_and_lists_them_as_pending()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);
        var context = Context();

        var operationId = Guid.CreateVersion7();
        var result = await pos.CaptureCashSaleAsync(context, Draft(operationId));

        Assert.True(result.Accepted);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal($"PROV-{operationId:N}", result.ProvisionalReference);

        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_lines;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(123400L, harness.ScalarLong("SELECT amount_scaled FROM local_cash_movements;"));

        var pending = await pos.GetPendingSalesAsync();
        var sale = Assert.Single(pending);
        Assert.Equal(operationId, sale.OperationId);
        Assert.Equal("Pending", sale.State);
        Assert.Equal(12.34m, sale.Amount);
        Assert.Single(sale.Lines);
    }

    [Fact]
    public async Task Due_operations_are_returned_by_the_retry_window()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var payload = "{\"type\":\"pos.sale.capture.v1\"}";

        var operation = new OutboxOperation
        {
            OperationId = Guid.CreateVersion7(),
            DeviceId = Guid.CreateVersion7(),
            BusinessId = Guid.CreateVersion7(),
            LocationId = Guid.CreateVersion7(),
            OperationType = "pos.sale.capture.v1",
            PayloadVersion = 1,
            CanonicalPayload = payload,
            Sha256Fingerprint = SqlitePayloadHash.Compute(payload),
            State = OutboxState.Pending,
            NextAttemptUtc = DateTime.UtcNow.AddHours(-1),
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
        };

        await harness.Store.EnqueueOutboxOperationAsync(operation);

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow, 50);
        Assert.Single(due);
        Assert.Equal(operation.OperationId, due[0].OperationId);

        // A window that ends before the retry time returns nothing.
        var notDue = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddHours(-2), 50);
        Assert.Empty(notDue);
    }

    [Fact]
    public async Task The_pending_list_and_summary_are_readable()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);

        await pos.CaptureCashSaleAsync(Context(), Draft(Guid.CreateVersion7()));

        var pending = await pos.GetPendingSalesAsync();
        Assert.Single(pending);

        var summary = await pos.GetQueueSummaryAsync();
        Assert.Equal(1, summary.Pending);
        Assert.Equal(0, summary.Rejected);

        var conflicts = await harness.Store.GetConflictsAsync();
        Assert.Empty(conflicts);

        var (revision, ordinal) = await harness.Store.GetSyncCursorAsync(Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.Equal(0L, revision);
        Assert.Equal(0L, ordinal);
    }

    [Fact]
    public async Task The_capture_survives_a_restart_and_is_still_pending()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);
        var operationId = Guid.CreateVersion7();

        await pos.CaptureCashSaleAsync(Context(), Draft(operationId));

        harness.Reopen();
        await harness.Store.InitializeAsync();
        var reopened = new LocalPosStore(harness.Store);

        var pending = await reopened.GetPendingSalesAsync();
        Assert.Single(pending);
        Assert.Equal(operationId, pending[0].OperationId);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));

        var summary = await reopened.GetQueueSummaryAsync();
        Assert.Equal(1, summary.Pending);
        Assert.Equal(0, summary.Rejected);
        Assert.Equal(12.34m, summary.ProvisionalCash);
    }

    [Fact]
    public async Task The_same_operation_id_never_creates_a_second_operation()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);
        var context = Context();
        var operationId = Guid.CreateVersion7();
        var draft = Draft(operationId);

        // A retry of the same checkout reuses the same operation id and the same payload.
        var first = await pos.CaptureCashSaleAsync(context, draft);
        var second = await pos.CaptureCashSaleAsync(context, draft);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
    }

    [Fact]
    public async Task The_same_operation_id_with_a_different_payload_is_refused()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);
        var context = Context();
        var operationId = Guid.CreateVersion7();

        var first = await pos.CaptureCashSaleAsync(context, Draft(operationId));
        Assert.True(first.Accepted);

        // Same key, different body: the original operation stays untouched and nothing is stored twice.
        var conflicting = await pos.CaptureCashSaleAsync(context, Draft(operationId, cash: 99m));

        Assert.False(conflicting.Accepted);
        Assert.Equal(LocalCaptureCodes.Rejected, conflicting.ErrorCode);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(123400L, harness.ScalarLong("SELECT amount_scaled FROM local_sale_drafts;"));
    }

    [Fact]
    public async Task A_refused_capture_stores_nothing_and_is_never_reported_as_success()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);

        // The cash does not cover the total.
        var notEnoughCash = await pos.CaptureCashSaleAsync(Context(), Draft(Guid.CreateVersion7(), cash: 1m));
        Assert.False(notEnoughCash.Accepted);
        Assert.Equal(LocalCaptureCodes.Rejected, notEnoughCash.ErrorCode);

        // An empty sale is refused as well.
        var empty = new LocalCashSaleDraft(Guid.CreateVersion7(), 10m, []);
        var noLines = await pos.CaptureCashSaleAsync(Context(), empty);
        Assert.False(noLines.Accepted);

        // A line that cannot be represented exactly is refused before anything is written.
        var imprecise = new LocalCashSaleDraft(
            Guid.CreateVersion7(),
            10m,
            [new LocalSaleLineDraft(0, Guid.CreateVersion7(), 0.0000001m, 5m, 0m, 0m, 0m, 1)]);
        var refused = await pos.CaptureCashSaleAsync(Context(), imprecise);
        Assert.False(refused.Accepted);

        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));
        Assert.Equal(0L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
    }

    [Fact]
    public async Task The_captured_payload_is_canonical_and_fingerprinted()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);
        var operationId = Guid.CreateVersion7();

        await pos.CaptureCashSaleAsync(Context(), Draft(operationId));

        var payload = harness.ScalarString("SELECT canonical_payload FROM outbox_operations;")!;
        var fingerprint = harness.ScalarString("SELECT sha256_fingerprint FROM outbox_operations;")!;

        Assert.Contains("\"type\":\"pos.sale.capture.v1\"", payload);
        Assert.Equal(SqlitePayloadHash.Compute(payload), fingerprint);
        Assert.Equal(64, fingerprint.Length);
    }

    [Fact]
    public async Task The_catalog_round_trips_through_the_cache()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();
        var pos = new LocalPosStore(harness.Store);
        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var count = await pos.ReplaceCatalogAsync(
            businessId,
            locationId,
            [
                new LocalCatalogItem(variantId, "SKU-1", "Coffee", 4.50m, 0.15m, 12m, true, 3),
            ]);

        Assert.Equal(1, count);

        var catalog = await pos.GetCatalogAsync();
        var item = Assert.Single(catalog);
        Assert.Equal(variantId, item.VariantId);
        Assert.Equal("SKU-1", item.Sku);
        Assert.Equal(4.50m, item.UnitPrice);
        Assert.Equal(0.15m, item.TaxRate);
        Assert.Equal(12m, item.OnHand);
    }

    [Fact]
    public async Task The_total_is_computed_from_the_captured_lines()
    {
        var lines = new List<LocalSaleLineDraft>
        {
            new(0, Guid.CreateVersion7(), 2m, 5.00m, 0.1m, 1.00m, 0.50m, 1),
            new(1, Guid.CreateVersion7(), 1m, 3.00m, 0m, 0m, 0m, 1),
        };

        // 2 × 5.00 − 0.50 + 1.00 tax = 10.50, plus 3.00 = 13.50
        Assert.Equal(13.50m, LocalPosStore.Total(lines));
    }
}
