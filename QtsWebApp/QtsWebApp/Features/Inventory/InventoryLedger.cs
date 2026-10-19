using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Sync;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Inventory;

/// <summary>
/// Single place where on-hand stock changes. The balance row is locked and the append-only movement is
/// created in the caller's transaction, so sale posting (QB-09), returns and stock takes all share the
/// same no-oversell guarantee without nested transactions.
/// </summary>
internal static class InventoryLedger
{
    public static async Task<InventoryMovement> ApplyAsync(
        BillingDbContext db,
        Guid businessId,
        Guid? userId,
        Guid locationId,
        Guid variantId,
        MovementType movementType,
        decimal quantityDelta,
        decimal unitCost,
        string referenceType,
        Guid referenceId,
        string? memo,
        DateTimeOffset now,
        bool allowNegative,
        CancellationToken cancellationToken)
    {
        var balance = await db.InventoryBalances
            .FromSql($"SELECT * FROM inventory_balances WHERE business_id = {businessId} AND location_id = {locationId} AND variant_id = {variantId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (balance is null)
        {
            balance = new InventoryBalance
            {
                BusinessId = businessId,
                LocationId = locationId,
                VariantId = variantId,
                OnHand = 0,
                LastMovementAtUtc = now,
            };

            db.InventoryBalances.Add(balance);
        }

        var resulting = balance.OnHand + quantityDelta;

        if (resulting < 0 && !allowNegative)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.InsufficientStock,
                "Not enough stock on hand.",
                $"On hand is {balance.OnHand}; the requested change is {quantityDelta}.");
        }

        balance.OnHand = resulting;
        balance.LastMovementAtUtc = now;

        // The stock projection travels in the same transaction as the movement, so a device never sees a
        // balance that was not committed (QB-21).
        await SyncProjections.StockAsync(db, businessId, locationId, variantId, resulting, cancellationToken)
            .ConfigureAwait(false);

        var movement = new InventoryMovement
        {
            BusinessId = businessId,
            LocationId = locationId,
            VariantId = variantId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            UnitCost = unitCost,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            OccurredAtUtc = now,
            CreatedByUserId = userId,
            Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
        };

        db.InventoryMovements.Add(movement);

        return movement;
    }
}
