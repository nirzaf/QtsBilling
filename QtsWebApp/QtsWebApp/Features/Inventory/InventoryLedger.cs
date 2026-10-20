using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Sync;
using QtsWebApp.Features.Orders;
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
        CancellationToken cancellationToken)
    {
        // The balance row is created with a scoped database upsert and only then locked. Creating it as a
        // tracked entity instead would let two concurrent transactions both believe they created it, and
        // the loser would fail late with a uniqueness error instead of serializing on the row
        // (specification D06, finding F17).
        var balance = await LockBalanceAsync(db, businessId, locationId, variantId, now, cancellationToken).ConfigureAwait(false);

        var resulting = balance.OnHand + quantityDelta;

        if (quantityDelta < 0m)
        {
            var reserved = await FulfillmentService.GetActiveReservedQuantityAsync(
                db, businessId, locationId, variantId, now, cancellationToken).ConfigureAwait(false);
            var available = balance.OnHand - reserved;
            if (Math.Abs(quantityDelta) > available)
            {
                throw new DomainRuleException(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.InsufficientStock,
                    "Not enough available stock.",
                    $"On hand is {balance.OnHand}, active reservations are {reserved}, and available stock is {available}; the requested change is {quantityDelta}.");
            }
        }

        // Negative stock is prohibited in this release, adjustments included: the database check and the
        // application now agree (specification D06, finding F18).
        if (resulting < 0)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.InsufficientStock,
                "Not enough stock on hand.",
                $"On hand is {balance.OnHand}; the requested change is {quantityDelta}. Negative stock is not permitted.");
        }

        // The value of the movement is the authority: the cost of goods sold is this value, never the
        // catalog price (specification D06, finding F15).
        decimal valueDelta;

        if (quantityDelta > 0m)
        {
            // The acquisition value of the stock that arrives.
            valueDelta = LedgerWriter.Round(quantityDelta * unitCost);
        }
        else if (quantityDelta < 0m)
        {
            if (balance.ValueOnHand <= 0m)
            {
                throw new DomainRuleException(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.ValuationRequired,
                    "The stock has no value.",
                    "The stock on hand carries no value, so the cost of this movement cannot be determined. " +
                    "Value the stock before consuming it.");
            }

            // The final depletion takes the exact remaining value, so a quantity of zero implies a value
            // of zero.
            valueDelta = resulting == 0m
                ? -balance.ValueOnHand
                : -LedgerWriter.Round(Math.Abs(quantityDelta) * balance.AverageUnitCost);
        }
        else
        {
            valueDelta = 0m;
        }

        balance.OnHand = resulting;
        balance.ValueOnHand = LedgerWriter.Round(balance.ValueOnHand + valueDelta);
        balance.AverageUnitCost = resulting > 0m
            ? Math.Round(balance.ValueOnHand / resulting, 8, MidpointRounding.AwayFromZero)
            : 0m;
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
            ValueDelta = valueDelta,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            OccurredAtUtc = now,
            CreatedByUserId = userId,
            Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
        };

        db.InventoryMovements.Add(movement);

        return movement;
    }

    /// <summary>Prepares and locks the authoritative balance for a compound inventory decision.</summary>
    public static async Task<InventoryBalance> LockBalanceAsync(
        BillingDbContext db,
        Guid businessId,
        Guid locationId,
        Guid variantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureBalanceRowAsync(db, businessId, locationId, variantId, now, cancellationToken).ConfigureAwait(false);
        var balance = await db.InventoryBalances
            .FromSql($"SELECT * FROM inventory_balances WHERE business_id = {businessId} AND location_id = {locationId} AND variant_id = {variantId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return balance ?? throw new DomainRuleException(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.Conflict,
            "The stock balance could not be prepared.",
            "The balance row for this location and variant could not be created. Retry the movement.");
    }

    /// <summary>
    /// Creates the balance row if it does not exist yet, without touching a row another transaction is
    /// holding. The insert is a no-op when the row is already there, so the following row lock is the
    /// only place where the transactions serialize.
    /// </summary>
    private static async Task EnsureBalanceRowAsync(
        BillingDbContext db,
        Guid businessId,
        Guid locationId,
        Guid variantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;

        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO inventory_balances
                    (id, business_id, location_id, variant_id, on_hand, last_movement_at_utc, created_at_utc, updated_at_utc, version)
                VALUES
                    (gen_random_uuid(), @business_id, @location_id, @variant_id, 0, @now, @now, @now, 1)
                ON CONFLICT (business_id, location_id, variant_id) DO NOTHING;
                """;

            AddParameter(command, "business_id", businessId);
            AddParameter(command, "location_id", locationId);
            AddParameter(command, "variant_id", variantId);
            AddParameter(command, "now", now);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
