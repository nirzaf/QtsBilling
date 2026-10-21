using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Inventory;

/// <summary>
/// Transactional authoritative inventory (QB-08). On-hand quantity is only ever changed together with an
/// append-only movement in the same transaction; the (reference type, reference id, movement kind) key makes
/// replays harmless, and negative stock is refused unless an authorized adjustment explicitly allows it
/// (F-02/F-12).
/// </summary>
public static class InventoryEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;
    private const int MaxReferenceTypeLength = 64;
    private const int MaxMemoLength = 2_000;
    private const int QuantityScale = 6;

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/locations", ListLocationsAsync)
            .WithTags("Inventory")
            .RequireAuthorization(Permissions.ProductsRead);

        var inventory = app.MapGroup("/api/v1/inventory").WithTags("Inventory");
        inventory.MapGet("/balances", ListBalancesAsync).RequireAuthorization(Permissions.ProductsRead);
        inventory.MapGet("/movements", ListMovementsAsync).RequireAuthorization(Permissions.ProductsRead);
        inventory.MapPost("/movements", RecordMovementAsync).RequireAuthorization(Permissions.InventoryAdjust);
        inventory.MapGet("/reconciliation", ReconciliationAsync).RequireAuthorization(Permissions.ReportsView);
        StockTransferEndpoints.MapStockTransferEndpoints(inventory);
        StockCountEndpoints.MapStockCountEndpoints(inventory);

        return app;
    }

    private static async Task<IResult> ListLocationsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var query = db.BusinessLocations.AsNoTracking().Where(location => location.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(location => location.IsActive);
        }

        var rows = await query
            .OrderBy(location => location.Name)
            .Select(location => new BusinessLocationDto(
                location.Id,
                location.Name,
                location.Code,
                location.Address,
                location.IsActive,
                location.Version,
                location.TimeZone))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows);
    }

    private static async Task<IResult> ListBalancesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid? locationId,
        string? search,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query =
            from balance in db.InventoryBalances.AsNoTracking()
            join variant in db.ProductVariants.AsNoTracking() on balance.VariantId equals variant.Id
            join location in db.BusinessLocations.AsNoTracking() on balance.LocationId equals location.Id
            where balance.BusinessId == businessId && variant.BusinessId == businessId && location.BusinessId == businessId
            select new { balance, variant, location };

        if (locationId is { } wantedLocation)
        {
            query = query.Where(row => row.balance.LocationId == wantedLocation);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(row =>
                EF.Functions.ILike(row.variant.Name, pattern)
                || (row.variant.Sku != null && EF.Functions.ILike(row.variant.Sku, pattern))
                || (row.variant.Barcode != null && EF.Functions.ILike(row.variant.Barcode, pattern)));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(row => row.balance.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(row => row.balance.Id)
            .Take(size + 1)
            .Select(row => new InventoryBalanceDto(
                row.balance.Id,
                row.location.Id,
                row.location.Name,
                row.variant.Id,
                row.variant.Name,
                row.variant.Sku,
                row.variant.Barcode,
                row.balance.OnHand,
                row.balance.LastMovementAtUtc,
                row.balance.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        return Results.Ok(new PagedResult<InventoryBalanceDto>(rows, nextCursor, size));
    }

    private static async Task<IResult> ListMovementsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid? variantId,
        Guid? locationId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query = db.InventoryMovements.AsNoTracking().Where(movement => movement.BusinessId == businessId);

        if (variantId is { } wantedVariant)
        {
            query = query.Where(movement => movement.VariantId == wantedVariant);
        }

        if (locationId is { } wantedLocation)
        {
            query = query.Where(movement => movement.LocationId == wantedLocation);
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(movement => movement.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(movement => movement.Id)
            .Take(size + 1)
            .Select(movement => new MovementRow(
                movement.Id,
                movement.LocationId,
                db.BusinessLocations.Where(location => location.Id == movement.LocationId).Select(location => location.Name).FirstOrDefault() ?? string.Empty,
                movement.VariantId,
                movement.MovementType,
                movement.QuantityDelta,
                movement.UnitCost,
                movement.ReferenceType,
                movement.ReferenceId,
                movement.OccurredAtUtc,
                movement.Memo,
                movement.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var page = rows
            .Select(row => new InventoryMovementDto(
                row.Id,
                row.LocationId,
                row.LocationName,
                row.VariantId,
                EnumBridge.To<StockMovementKind>(row.MovementType),
                row.QuantityDelta,
                row.UnitCost,
                row.ReferenceType,
                row.ReferenceId,
                row.OccurredAtUtc,
                row.Memo,
                row.CreatedAtUtc))
            .ToList();

        return Results.Ok(new PagedResult<InventoryMovementDto>(page, nextCursor, size));
    }

    /// <summary>
    /// Applies one movement and its balance change atomically. The balance row is locked, so two concurrent
    /// requests for the last unit cannot both succeed; the loser gets 409 INSUFFICIENT_STOCK.
    /// </summary>
    private static async Task<IResult> RecordMovementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        RecordMovementRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var movementType = (MovementType)request.MovementType;
        var quantity = request.QuantityDelta;
        var referenceType = request.ReferenceType?.Trim().ToLowerInvariant() ?? string.Empty;
        var errors = new Dictionary<string, string[]>();

        if (movementType == MovementType.Sale)
        {
            errors["movementType"] = ["Sales post through the sale posting endpoint, not the inventory API."];
        }

        if (referenceType.Length is 0 or > MaxReferenceTypeLength)
        {
            errors["referenceType"] = [$"A reference type of 1-{MaxReferenceTypeLength} characters is required."];
        }

        if (quantity == 0)
        {
            errors["quantityDelta"] = ["A movement must change stock by a non-zero amount."];
        }
        else if (decimal.Round(quantity, QuantityScale) != quantity)
        {
            errors["quantityDelta"] = [$"Quantities support at most {QuantityScale} decimal places."];
        }
        else if (RequirePositive(movementType) && quantity < 0)
        {
            errors["quantityDelta"] = [$"{movementType} increases stock; send a positive quantity."];
        }
        else if (RequireNegative(movementType) && quantity > 0)
        {
            errors["quantityDelta"] = [$"{movementType} decreases stock; send a negative quantity."];
        }

        if (request.UnitCost < 0)
        {
            errors["unitCost"] = ["Unit cost must not be negative."];
        }

        if (request.Memo is { Length: > MaxMemoLength })
        {
            errors["memo"] = [$"A memo of at most {MaxMemoLength} characters is supported."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The inventory movement is invalid.", errors);
        }

        var variantOk = await db.ProductVariants.AsNoTracking()
            .AnyAsync(variant => variant.BusinessId == businessId && variant.Id == request.VariantId, cancellationToken)
            .ConfigureAwait(false);

        if (!variantOk)
        {
            return ApiResults.ValidationProblem(
                "The inventory movement is invalid.",
                new Dictionary<string, string[]> { ["variantId"] = ["The variant does not exist in this business."] });
        }

        var locationOk = await db.BusinessLocations.AsNoTracking()
            .AnyAsync(location => location.BusinessId == businessId && location.Id == request.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (!locationOk)
        {
            return ApiResults.ValidationProblem(
                "The inventory movement is invalid.",
                new Dictionary<string, string[]> { ["locationId"] = ["The location does not exist in this business."] });
        }

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this inventory location.");
        }

        var duplicate = await db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId
                && movement.ReferenceType == referenceType
                && movement.ReferenceId == request.ReferenceId
                && movement.MovementType == movementType)
            .Select(movement => movement.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (duplicate != Guid.Empty)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyKeyReused,
                "This inventory reference was already applied.",
                $"Movement {duplicate} already records ({referenceType}, {request.ReferenceId}, {movementType}); a replay must not double-count stock.");
        }

        try
        {
            var movement = await ApplyMovementAsync(
                db,
                businessId,
                userId,
                movementType,
                request,
                referenceType,
                quantity,
                cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/inventory/movements/{movement.Id}",
                new InventoryMovementDto(
                    movement.Id,
                    movement.LocationId,
                    await db.BusinessLocations.AsNoTracking()
                        .Where(location => location.Id == movement.LocationId)
                        .Select(location => location.Name)
                        .FirstAsync(cancellationToken)
                        .ConfigureAwait(false),
                    movement.VariantId,
                    EnumBridge.To<StockMovementKind>(movement.MovementType),
                    movement.QuantityDelta,
                    movement.UnitCost,
                    movement.ReferenceType,
                    movement.ReferenceId,
                    movement.OccurredAtUtc,
                    movement.Memo,
                    movement.CreatedAtUtc));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<InventoryMovement> ApplyMovementAsync(
        BillingDbContext db,
        Guid businessId,
        Guid userId,
        MovementType movementType,
        RecordMovementRequest request,
        string referenceType,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry must not see state tracked by an aborted attempt.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var movement = await InventoryLedger.ApplyAsync(
                db,
                businessId,
                userId,
                request.LocationId,
                request.VariantId,
                movementType,
                quantity,
                request.UnitCost,
                referenceType,
                request.ReferenceId,
                request.Memo,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return movement;
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> ReconciliationAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var mismatches = await db.Database.SqlQuery<ReconciliationRow>(
            $"""
            SELECT b.location_id AS "LocationId", b.variant_id AS "VariantId", b.on_hand AS "OnHand", COALESCE(m.total, 0) AS "MovementSum"
            FROM inventory_balances b
            LEFT JOIN (
                SELECT location_id, variant_id, SUM(quantity_delta) AS total
                FROM inventory_movements
                WHERE business_id = {businessId}
                GROUP BY location_id, variant_id
            ) m ON m.location_id = b.location_id AND m.variant_id = b.variant_id
            WHERE b.business_id = {businessId} AND b.on_hand <> COALESCE(m.total, 0)
            """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var totalOnHand = await db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == businessId)
            .SumAsync(balance => balance.OnHand, cancellationToken)
            .ConfigureAwait(false);

        var totalMovements = await db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId)
            .SumAsync(movement => movement.QuantityDelta, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new InventoryReconciliationDto(
            mismatches.Count,
            totalOnHand,
            totalMovements,
            mismatches
                .Select(row => new InventoryReconciliationRowDto(row.LocationId, row.VariantId, row.OnHand, row.MovementSum))
                .ToList()));
    }

    private static bool RequirePositive(MovementType type)
        => type is MovementType.Opening
            or MovementType.PurchaseReceipt
            or MovementType.SaleReversal
            or MovementType.TransferIn;

    private static bool RequireNegative(MovementType type)
        => type is MovementType.PurchaseReturn
            or MovementType.TransferOut;

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName?.Contains("inventory_movements", StringComparison.Ordinal) == true =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "This inventory reference was already applied."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The inventory write conflicts with an existing record; retry the request."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.InsufficientStock,
                    "The resulting on-hand quantity violates inventory integrity guards."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The movement references a variant or location outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The inventory write failed."),
        };

    /// <summary>Projection shape carrying the stored movement kind; the wire enum is bridged in memory.</summary>
    private sealed record MovementRow(
        Guid Id,
        Guid LocationId,
        string LocationName,
        Guid VariantId,
        MovementType MovementType,
        decimal QuantityDelta,
        decimal UnitCost,
        string ReferenceType,
        Guid ReferenceId,
        DateTimeOffset OccurredAtUtc,
        string? Memo,
        DateTimeOffset CreatedAtUtc);

    /// <summary>Materialization shape for the reconciliation query (column names map by convention).</summary>
    private sealed class ReconciliationRow
    {
        public Guid LocationId { get; set; }

        public Guid VariantId { get; set; }

        public decimal OnHand { get; set; }

        public decimal MovementSum { get; set; }
    }
}
