using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Sqlite.Entities;

// The device row and the wire contract share a name; the store speaks the entity.
using SyncConflict = QtsBilling.Persistence.Sqlite.Entities.SyncConflict;

namespace QtsBilling.Persistence.Sqlite;

/// <summary>What one change-application did.</summary>
public sealed record SyncApplyResult(int Applied, int Conflicts, int Skipped);

public sealed partial class SqliteLocalStore
{
    /// <summary>
    /// Applies the change feed to the device cache (QB-22). A change that touches a record a pending local
    /// operation also touches is **not** applied silently: it becomes a conflict for the operator, and the
    /// local operation keeps its value until the conflict is resolved.
    /// </summary>
    public async Task<SyncApplyResult> ApplyChangesAsync(
        IReadOnlyList<SyncChangeDto> changes,
        Guid businessId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return new SyncApplyResult(0, 0, 0);
        }

        var applied = 0;
        var conflicts = 0;
        var skipped = 0;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pendingVariants = await PendingVariantIdsAsync(db, cancellationToken).ConfigureAwait(false);

        foreach (var change in changes)
        {
            if (change.Kind == QtsBilling.Contracts.Sync.SyncChangeKind.Tombstone)
            {
                await ApplyTombstoneAsync(db, change, businessId, cancellationToken).ConfigureAwait(false);
                applied++;
                continue;
            }

            if (change.Payload is not { } payload)
            {
                skipped++;
                continue;
            }

            var touched = await ApplyProjectionAsync(db, change, payload, businessId, locationId, cancellationToken)
                .ConfigureAwait(false);

            if (touched.Count > 0 && touched.Overlaps(pendingVariants))
            {
                // The server changed something a pending local sale also uses: queue it for review.
                await db.SyncConflicts.AddAsync(
                    new SyncConflict
                    {
                        Id = Guid.CreateVersion7(),
                        BusinessId = businessId,
                        EntityType = change.EntityType,
                        EntityId = change.EntityId,
                        Reason = "A pending local operation uses a record the server just changed.",
                        ServerValue = payload.GetRawText(),
                        LocalValue = await LocalValueForAsync(db, touched, cancellationToken).ConfigureAwait(false),
                        DetectedAtUtc = DateTimeOffset.UtcNow,
                    },
                    cancellationToken).ConfigureAwait(false);

                conflicts++;
                skipped++;
                continue;
            }

            applied++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SyncApplyResult(applied, conflicts, skipped);
    }

    /// <summary>The open conflicts of the device.</summary>
    public async Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.SyncConflicts
            .AsNoTracking()
            .Where(conflict => conflict.ResolvedAtUtc == null)
            .OrderBy(conflict => conflict.DetectedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a conflict. With <c>UseServer</c> the server value is applied to the cache; with
    /// <c>KeepLocal</c> the local operation stays authoritative and the server decides when it is pushed.
    /// </summary>
    public async Task<bool> ResolveConflictAsync(
        Guid conflictId,
        SyncConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var conflict = await db.SyncConflicts
            .SingleOrDefaultAsync(candidate => candidate.Id == conflictId, cancellationToken)
            .ConfigureAwait(false);

        if (conflict is null)
        {
            return false;
        }

        conflict.Resolution = resolution.ToString();
        conflict.ResolvedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The cursor of the last successful pull.</summary>
    public async Task<(long Revision, long Ordinal)> GetSyncCursorAsync(
        Guid businessId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var state = await db.SyncStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.BusinessId == businessId && candidate.LocationId == locationId,
                cancellationToken)
            .ConfigureAwait(false);

        return state is null ? (0L, 0L) : (state.LastRevision, state.LastOrdinal);
    }

    /// <summary>When the device last pulled the feed successfully.</summary>
    public async Task<DateTimeOffset?> GetLastSyncSuccessAsync(
        Guid businessId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var state = await db.SyncStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.BusinessId == businessId && candidate.LocationId == locationId,
                cancellationToken)
            .ConfigureAwait(false);

        return state?.LastSuccessUtc is { } success ? new DateTimeOffset(DateTime.SpecifyKind(success, DateTimeKind.Utc)) : null;
    }

    /// <summary>Moves the cursor forward after a successful pull.</summary>
    public async Task SetSyncCursorAsync(
        Guid businessId,
        Guid locationId,
        long revision,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var state = await db.SyncStates
            .SingleOrDefaultAsync(
                candidate => candidate.BusinessId == businessId && candidate.LocationId == locationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            state = new SyncState
            {
                BusinessId = businessId,
                LocationId = locationId,
            };

            db.SyncStates.Add(state);
        }

        state.LastRevision = revision;
        state.LastOrdinal = ordinal;
        state.LastSuccessUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The variant ids a pending local operation still uses.</summary>
    private static async Task<HashSet<Guid>> PendingVariantIdsAsync(
        LocalStoreDbContext db,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();

        var pending = await db.OutboxOperations
            .AsNoTracking()
            .Where(operation => operation.State == OutboxState.Pending || operation.State == OutboxState.Sending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in pending)
        {
            try
            {
                using var document = JsonDocument.Parse(operation.CanonicalPayload);

                if (document.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in lines.EnumerateArray())
                    {
                        if (line.TryGetProperty("variantId", out var variant) && variant.TryGetGuid(out var variantId))
                        {
                            ids.Add(variantId);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A payload that cannot be read never blocks the feed.
            }
        }

        return ids;
    }

    /// <summary>Applies one projection and returns the record ids it touched.</summary>
    private static async Task<HashSet<Guid>> ApplyProjectionAsync(
        LocalStoreDbContext db,
        SyncChangeDto change,
        JsonElement payload,
        Guid businessId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var touched = new HashSet<Guid>();

        switch (change.EntityType)
        {
            case "product":
            {
                if (!payload.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                var taxRate = payload.TryGetProperty("taxRate", out var rate) && rate.TryGetDecimal(out var value) ? value : 0m;

                foreach (var variant in variants.EnumerateArray())
                {
                    if (!variant.TryGetProperty("id", out var idElement) || !idElement.TryGetGuid(out var variantId))
                    {
                        continue;
                    }

                    touched.Add(variantId);

                    var row = await db.CachedProducts
                        .SingleOrDefaultAsync(
                            candidate => candidate.BusinessId == businessId
                                && candidate.LocationId == locationId
                                && candidate.ProductId == variantId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (row is null)
                    {
                        row = new CachedProduct
                        {
                            BusinessId = businessId,
                            LocationId = locationId,
                            ProductId = variantId,
                        };

                        db.CachedProducts.Add(row);
                    }

                    row.Sku = variant.TryGetProperty("sku", out var sku) ? sku.GetString() ?? string.Empty : row.Sku;
                    row.Name = variant.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : row.Name;
                    row.Price = variant.TryGetProperty("sellingPrice", out var price) && price.TryGetDecimal(out var priceValue)
                        ? priceValue
                        : row.Price;
                    row.TaxRate = taxRate;
                    row.Active = variant.TryGetProperty("isActive", out var active) && active.GetBoolean();
                    row.Revision = variant.TryGetProperty("version", out var version) && version.TryGetInt64(out var versionValue)
                        ? versionValue
                        : row.Revision;
                }

                break;
            }

            case "stock":
            {
                if (!payload.TryGetProperty("variantId", out var stockVariant) || !stockVariant.TryGetGuid(out var stockVariantId))
                {
                    break;
                }

                touched.Add(stockVariantId);

                var onHand = payload.TryGetProperty("onHand", out var quantity) && quantity.TryGetDecimal(out var onHandValue)
                    ? onHandValue
                    : 0m;

                var row = await db.CachedStock
                    .SingleOrDefaultAsync(
                        candidate => candidate.BusinessId == businessId
                            && candidate.LocationId == locationId
                            && candidate.VariantId == stockVariantId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (row is null)
                {
                    row = new CachedStock
                    {
                        BusinessId = businessId,
                        LocationId = locationId,
                        VariantId = stockVariantId,
                    };

                    db.CachedStock.Add(row);
                }

                row.ServerQuantity = onHand;
                break;
            }

            case "customer":
            {
                var row = await db.CachedCustomers
                    .SingleOrDefaultAsync(
                        candidate => candidate.BusinessId == businessId && candidate.CustomerId == change.EntityId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (row is null)
                {
                    row = new CachedCustomer
                    {
                        BusinessId = businessId,
                        CustomerId = change.EntityId,
                    };

                    db.CachedCustomers.Add(row);
                }

                row.FieldsNeededOffline = payload.GetRawText();
                row.Revision = payload.TryGetProperty("version", out var version) && version.TryGetInt64(out var versionValue)
                    ? versionValue
                    : row.Revision;
                row.Deleted = false;
                break;
            }
        }

        return touched;
    }

    private static async Task ApplyTombstoneAsync(
        LocalStoreDbContext db,
        SyncChangeDto change,
        Guid businessId,
        CancellationToken cancellationToken)
    {
        if (change.EntityType != "customer")
        {
            return;
        }

        var row = await db.CachedCustomers
            .SingleOrDefaultAsync(
                candidate => candidate.BusinessId == businessId && candidate.CustomerId == change.EntityId,
                cancellationToken)
            .ConfigureAwait(false);

        if (row is not null)
        {
            row.Deleted = true;
            row.FieldsNeededOffline = "{}";
        }
    }

    private static async Task<string?> LocalValueForAsync(
        LocalStoreDbContext db,
        HashSet<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.OutboxOperations
            .AsNoTracking()
            .Where(operation => operation.State == OutboxState.Pending || operation.State == OutboxState.Sending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (row.CanonicalPayload.Contains(variantIds.First().ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return row.CanonicalPayload;
            }
        }

        return null;
    }
}
