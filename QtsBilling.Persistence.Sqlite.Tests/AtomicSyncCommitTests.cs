using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Sqlite.Entities;
using Xunit;

namespace QtsBilling.Persistence.Sqlite.Tests;

public sealed class AtomicSyncCommitTests
{
    [Fact]
    public async Task Pull_applies_changes_and_advances_cursor_as_one_commit()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        await harness.Store.SetSyncCursorAsync(businessId, locationId, 7, 2);

        var result = await harness.Store.ApplyChangesAndAdvanceCursorAsync(
            [CustomerChange(customerId, "New customer", revision: 8)],
            businessId,
            locationId,
            nextRevision: 8,
            nextOrdinal: 0);

        Assert.Equal(new SyncApplyResult(1, 0, 0), result);
        Assert.Equal((8L, 0L), await harness.Store.GetSyncCursorAsync(businessId, locationId));
        Assert.Equal("{\"name\":\"New customer\",\"version\":8}",
            harness.ScalarString("SELECT fields_needed_offline FROM cached_customers;"));
    }

    [Fact]
    public async Task Pull_rolls_back_both_cache_and_cursor_when_commit_fails()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        await harness.Store.SetSyncCursorAsync(businessId, locationId, 7, 2);
        await using (var db = harness.CreateContext())
        {
            db.CachedCustomers.Add(new CachedCustomer
            {
                BusinessId = businessId,
                CustomerId = customerId,
                FieldsNeededOffline = "{\"name\":\"Old customer\",\"version\":7}",
                Revision = 7,
            });
            await db.SaveChangesAsync();
        }

        harness.Execute("CREATE TRIGGER fail_sync_cursor_update AFTER UPDATE ON sync_state " +
            "BEGIN SELECT RAISE(ABORT, 'forced cursor failure'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Store.ApplyChangesAndAdvanceCursorAsync(
            [CustomerChange(customerId, "New customer", revision: 8)],
            businessId,
            locationId,
            nextRevision: 8,
            nextOrdinal: 0));

        Assert.Equal((7L, 2L), await harness.Store.GetSyncCursorAsync(businessId, locationId));
        Assert.Equal("{\"name\":\"Old customer\",\"version\":7}",
            harness.ScalarString("SELECT fields_needed_offline FROM cached_customers;"));
    }

    [Fact]
    public async Task Resync_cache_rebuild_and_cursor_reset_roll_back_together()
    {
        await using var harness = LocalStoreHarness.Create();
        await harness.Store.InitializeAsync();

        var businessId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        await harness.Store.SetSyncCursorAsync(businessId, locationId, 12, 4);
        await harness.Store.ReplaceCachedProductsAsync(businessId, locationId,
        [
            new CachedProduct
            {
                BusinessId = businessId,
                LocationId = locationId,
                ProductId = Guid.CreateVersion7(),
                Sku = "A-1",
                Name = "Retained on rollback",
                Price = 4m,
                TaxRate = 0m,
                TaxMode = "Exclusive",
                Active = true,
                Revision = 1,
            },
        ]);

        harness.Execute("CREATE TRIGGER fail_sync_cursor_update AFTER UPDATE ON sync_state " +
            "BEGIN SELECT RAISE(ABORT, 'forced cursor failure'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Store.RebuildCacheAsync(businessId, locationId));

        Assert.Equal(1, await harness.Store.GetCachedProductCountAsync());
        Assert.Equal((12L, 4L), await harness.Store.GetSyncCursorAsync(businessId, locationId));
    }

    private static SyncChangeDto CustomerChange(Guid customerId, string name, long revision)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { name, version = revision }));
        return new SyncChangeDto(
            revision,
            Ordinal: 0,
            EntityType: "customer",
            EntityId: customerId,
            SyncChangeKind.Upsert,
            document.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }
}
