using QtsBilling.Persistence.Sqlite.Entities;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class ReceiptAndSyncStateTests
{
    [Fact]
    public async Task Posted_receipt_is_idempotent_and_updates_outbox_and_money_states()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        var receipt = new InboxReceipt
        {
            OperationId = operationId,
            ServerResult = "Posted",
            ServerSaleId = Guid.CreateVersion7(),
            PostedNumber = "S-2026-00042",
            ServerRevision = 42,
            AppliedAtUtc = DateTime.UtcNow,
        };

        await harness.Store.ApplyReceiptAsync(receipt);

        Assert.Equal("Acknowledged", harness.ScalarString("SELECT state FROM outbox_operations;"));
        Assert.Equal("Posted", harness.ScalarString("SELECT state FROM local_sale_drafts;"));
        Assert.Equal("Posted", harness.ScalarString("SELECT state FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM inbox_receipts;"));

        // Replay of the identical receipt must not duplicate anything.
        await harness.Store.ApplyReceiptAsync(receipt);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM inbox_receipts;"));

        var stored = await harness.Store.GetInboxReceiptAsync(operationId);
        Assert.NotNull(stored);
        Assert.Equal("S-2026-00042", stored!.PostedNumber);

        var summary = await harness.Store.GetPendingSummaryAsync();
        Assert.Equal(0, summary.PendingOperations);
        Assert.Equal(0m, summary.ProvisionalCashAtRisk);

        await Assert.ThrowsAsync<SqliteReceiptConflictException>(
            () => harness.Store.ApplyReceiptAsync(new InboxReceipt
            {
                OperationId = operationId,
                ServerResult = "NeedsReview",
                ErrorCode = "STOCK_CONFLICT",
                AppliedAtUtc = DateTime.UtcNow,
            }));
    }

    [Fact]
    public async Task Rejected_receipt_keeps_the_money_records_for_review()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        await harness.Store.ApplyReceiptAsync(new InboxReceipt
        {
            OperationId = operationId,
            ServerResult = "NeedsReview",
            ErrorCode = "STOCK_CONFLICT",
            AppliedAtUtc = DateTime.UtcNow,
        });

        Assert.Equal("Rejected", harness.ScalarString("SELECT state FROM outbox_operations;"));
        Assert.Equal("Rejected", harness.ScalarString("SELECT state FROM local_sale_drafts;"));
        Assert.Equal("STOCK_CONFLICT", harness.ScalarString("SELECT last_error FROM local_sale_drafts;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_sale_drafts;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM local_cash_movements;"));
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM inbox_receipts;"));

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddDays(1));
        Assert.Empty(due);

        var summary = await harness.Store.GetPendingSummaryAsync();
        Assert.Equal(0, summary.PendingOperations);
        Assert.Equal(1, summary.RejectedOperations);
        Assert.Equal(0m, summary.ProvisionalCashAtRisk);
    }

    [Fact]
    public async Task Transient_retry_receipt_keeps_the_operation_pending()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operationId = Guid.CreateVersion7();
        await harness.Store.CaptureProvisionalCashSaleAsync(TestData.Sale(operationId));

        await harness.Store.ApplyReceiptAsync(new InboxReceipt
        {
            OperationId = operationId,
            ServerResult = "TransientRetry",
            ErrorCode = "UNKNOWN",
            AppliedAtUtc = DateTime.UtcNow,
        });

        Assert.Equal("Pending", harness.ScalarString("SELECT state FROM outbox_operations;"));
        Assert.Equal("Pending", harness.ScalarString("SELECT state FROM local_sale_drafts;"));

        var due = await harness.Store.GetDueOutboxAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.Single(due);
    }

    [Fact]
    public async Task Device_profile_and_sync_state_round_trip()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var deviceId = Guid.CreateVersion7();
        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();

        await harness.Store.SaveDeviceProfileAsync(new DeviceProfile
        {
            DeviceId = deviceId,
            BusinessId = businessId,
            LocationId = locationId,
            EnrolledAtUtc = DateTime.UtcNow,
        });

        var profile = await harness.Store.GetDeviceProfileAsync();
        Assert.NotNull(profile);
        Assert.Equal(deviceId, profile!.DeviceId);
        Assert.Equal(4, profile.SchemaVersion);

        await harness.Store.SaveSyncStateAsync(new SyncState
        {
            BusinessId = businessId,
            LocationId = locationId,
            LastRevision = 10,
            LastOrdinal = 2,
            LastSuccessUtc = DateTime.UtcNow,
            BaselineVersion = 1,
        });

        var state = await harness.Store.GetSyncStateAsync(businessId, locationId);
        Assert.NotNull(state);
        Assert.Equal(10, state!.LastRevision);
        Assert.Equal(2, state.LastOrdinal);

        await harness.Store.SaveSyncStateAsync(new SyncState
        {
            BusinessId = businessId,
            LocationId = locationId,
            LastRevision = 25,
            LastOrdinal = 0,
            LastSuccessUtc = DateTime.UtcNow,
            BaselineVersion = 2,
        });

        var updated = await harness.Store.GetSyncStateAsync(businessId, locationId);
        Assert.NotNull(updated);
        Assert.Equal(25, updated!.LastRevision);
        Assert.Equal(2, updated.BaselineVersion);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM sync_state;"));
    }

    [Fact]
    public async Task Non_sale_operations_enqueue_and_replay_safely()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var operation = TestData.Operation();
        var stored = await harness.Store.EnqueueOutboxOperationAsync(operation);
        Assert.Equal(operation.OperationId, stored.OperationId);
        Assert.Equal(SqlitePayloadHash.Compute(operation.CanonicalPayload), stored.Sha256Fingerprint);

        var replay = TestData.Operation(
            operationId: operation.OperationId,
            businessId: operation.BusinessId,
            locationId: operation.LocationId,
            deviceId: operation.DeviceId,
            payload: operation.CanonicalPayload);
        await harness.Store.EnqueueOutboxOperationAsync(replay);
        Assert.Equal(1L, harness.ScalarLong("SELECT COUNT(*) FROM outbox_operations;"));

        var tampered = TestData.Operation(
            operationId: operation.OperationId,
            businessId: operation.BusinessId,
            locationId: operation.LocationId,
            deviceId: operation.DeviceId,
            payload: "{\"type\":\"cash.session.open.v1\",\"tampered\":true}");
        await Assert.ThrowsAsync<SqliteSameKeyDifferentPayloadException>(
            () => harness.Store.EnqueueOutboxOperationAsync(tampered));
    }
}
