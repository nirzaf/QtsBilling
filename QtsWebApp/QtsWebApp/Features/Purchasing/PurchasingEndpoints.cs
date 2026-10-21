using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Purchasing;

/// <summary>
/// Purchasing integrity (QB-12): a purchase order is a commitment only - it never moves stock or the
/// ledger. Stock changes exclusively through receipts, each of which is one document with an idempotency
/// receipt, so a retried delivery cannot increase stock twice, and quantities are validated against the
/// ordered and already-received amounts under a row lock. Returns reverse received quantities through the
/// same controlled path.
/// </summary>
public static class PurchasingEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string OrderSequenceName = "purchase_order";
    private const string OrderSequencePrefix = "PO-";
    private const string ReceiptSequenceName = "goods_receipt";
    private const string ReceiptSequencePrefix = "GRN-";
    private const string ReturnSequenceName = "purchase_return";
    private const string ReturnSequencePrefix = "PR-";

    /// <summary>The inventory account a receipt capitalises and a return relieves.</summary>
    private const string InventoryAccountCode = "1200";

    /// <summary>
    /// The clearing account for goods that are received but not yet invoiced. A receipt is only a
    /// commitment until the supplier bill arrives: the value sits here, not in a supplier payable
    /// (specification D08, finding F16).
    /// </summary>
    private const string GrniAccountCode = "2101";
    private const int MaxLines = 100;
    private const int MaxNameLength = 200;
    private const int MaxReferenceLength = 128;
    private const int MaxReasonLength = 500;
    private const int MaxNoteLength = 2_000;
    private const int MaxLinesPerRequest = 200;

    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        var suppliers = app.MapGroup("/api/v1/suppliers").WithTags("Purchasing");

        suppliers.MapGet("/", ListSuppliersAsync).RequireAuthorization(Permissions.PurchasingManage);
        suppliers.MapPost("/", CreateSupplierAsync).RequireAuthorization(Permissions.PurchasingManage);
        suppliers.MapGet("/{id:guid}", GetSupplierAsync).RequireAuthorization(Permissions.PurchasingManage);
        suppliers.MapPut("/{id:guid}", UpdateSupplierAsync).RequireAuthorization(Permissions.PurchasingManage);
        suppliers.MapDelete("/{id:guid}", DeleteSupplierAsync).RequireAuthorization(Permissions.PurchasingManage);

        var orders = app.MapGroup("/api/v1/purchase-orders").WithTags("Purchasing");

        orders.MapGet("/", ListOrdersAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapPost("/", CreateOrderAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapGet("/{id:guid}", GetOrderAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapPut("/{id:guid}", UpdateOrderAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapDelete("/{id:guid}", DeleteOrderAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapPost("/{id:guid}/order", ConfirmOrderAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapPost("/{id:guid}/cancel", CancelOrderAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapPost("/{id:guid}/receipts", ReceiveAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapPost("/{id:guid}/returns", ReturnAsync).RequireAuthorization(Permissions.PurchasingManage);
        orders.MapGet("/{id:guid}/receipts", ListReceiptsAsync).RequireAuthorization(Permissions.PurchasingManage);

        return app;
    }

    // ---------------------------------------------------------------- suppliers

    private static async Task<IResult> ListSuppliersAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? search,
        bool? includeInactive,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.Suppliers.AsNoTracking().Where(supplier => supplier.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(supplier => supplier.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(supplier => EF.Functions.ILike(supplier.Name, pattern));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(supplier => supplier.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(supplier => supplier.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        return Results.Ok(new PagedResult<SupplierDto>(rows.Select(ToSupplierDto).ToList(), nextCursor, size));
    }

    private static async Task<IResult> CreateSupplierAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveSupplierRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var errors = ValidateSupplier(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The supplier payload is invalid.", errors);
        }

        var supplier = new Supplier
        {
            BusinessId = businessId,
            Name = request.Name.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
            IsActive = true,
        };

        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Created($"/api/v1/suppliers/{supplier.Id}", ToSupplierDto(supplier));
    }

    private static async Task<IResult> GetSupplierAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var supplier = await db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return supplier is null ? NotFoundSupplier() : Results.Ok(ToSupplierDto(supplier));
    }

    private static async Task<IResult> UpdateSupplierAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpRequest httpRequest,
        SaveSupplierRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (supplier is null)
        {
            return NotFoundSupplier();
        }

        // The same rule the customer workbench applies: an editor that read the record must send the version it
        // saw, so two people correcting one supplier cannot quietly overwrite each other (task 15, T15.03).
        if (httpRequest.Headers.TryGetValue("If-Match", out var ifMatch)
            && long.TryParse(ifMatch.ToString(), out var expectedVersion)
            && expectedVersion != supplier.Version)
        {
            return SupplierConcurrencyConflict(supplier.Version);
        }

        var errors = ValidateSupplier(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The supplier payload is invalid.", errors);
        }

        supplier.Name = request.Name.Trim();
        supplier.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        supplier.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        supplier.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SupplierConcurrencyConflict(supplier.Version);
        }

        return Results.Ok(ToSupplierDto(supplier));
    }

    private static async Task<IResult> DeleteSupplierAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (supplier is null)
        {
            return NotFoundSupplier();
        }

        // Soft delete keeps purchase history intact; inactive suppliers cannot be picked for new orders (D-29 pattern).
        supplier.IsActive = false;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static Dictionary<string, string[]> ValidateSupplier(SaveSupplierRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (request.Email is { Length: > 320 })
        {
            errors["email"] = ["An email of at most 320 characters is supported."];
        }

        if (request.Phone is { Length: > 32 })
        {
            errors["phone"] = ["A phone of at most 32 characters is supported."];
        }

        return errors;
    }

    private static SupplierDto ToSupplierDto(Supplier supplier)
        => new(
            supplier.Id,
            supplier.Name,
            supplier.Email,
            supplier.Phone,
            supplier.Address,
            supplier.IsActive,
            supplier.Version,
            supplier.CreatedAtUtc,
            supplier.UpdatedAtUtc);

    private static IResult NotFoundSupplier()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The supplier does not exist in this business.");

    /// <summary>A lost edit race says so, and names the version that won, so the screen can re-read and re-apply.</summary>
    private static IResult SupplierConcurrencyConflict(long storedVersion)
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.OptimisticConcurrencyFailure,
            "Someone changed this supplier first.",
            $"The stored version is {storedVersion}. Reload the supplier and repeat the correction you still intend.");

    // ---------------------------------------------------------------- purchase orders

    private static async Task<IResult> ListOrdersAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? status,
        Guid? supplierId,
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

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.PurchaseOrders.AsNoTracking().Where(order => order.BusinessId == businessId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<PurchaseOrderStatus>(status.Trim(), ignoreCase: true, out var parsed))
            {
                return ApiResults.ValidationProblem(
                    "The filter is invalid.",
                    new Dictionary<string, string[]> { ["status"] = [$"Unknown purchase order status '{status}'."] });
            }

            query = query.Where(order => order.Status == parsed);
        }

        if (supplierId is { } supplier)
        {
            query = query.Where(order => order.SupplierId == supplier);
        }

        if (locationId is { } location)
        {
            query = query.Where(order => order.LocationId == location);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(order => EF.Functions.ILike(order.OrderNumber, pattern));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(order => order.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(order => order.Id)
            .Take(size + 1)
            .Select(order => new OrderRow(
                order.Id,
                order.OrderNumber,
                order.Status,
                order.SupplierId,
                order.LocationId,
                order.OrderDate,
                order.ExpectedDate,
                order.Currency,
                order.SubtotalAmount,
                order.DiscountAmount,
                order.TaxAmount,
                order.TotalAmount,
                order.Note,
                order.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var supplierIds = rows.Select(row => row.SupplierId).Distinct().ToArray();

        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(candidate => candidate.BusinessId == businessId && supplierIds.Contains(candidate.Id))
            .Select(candidate => new { candidate.Id, candidate.Name })
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.Name, cancellationToken)
            .ConfigureAwait(false);

        var page = rows
            .Select(row => ToSummary(
                row,
                suppliers.TryGetValue(row.SupplierId, out var name) ? name : null))
            .ToList();

        return Results.Ok(new PagedResult<PurchaseOrderSummaryDto>(page, nextCursor, size));
    }

    private static async Task<IResult> CreateOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SavePurchaseOrderRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var prepared = await PrepareOrderAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var lines = prepared.Lines!;
        var totals = TotalsOf(lines);
        var orderDate = request.OrderDate ?? LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var order = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var number = await LedgerWriter.AllocateNumberAsync(
                        db,
                        businessId,
                        OrderSequenceName,
                        OrderSequencePrefix,
                        orderDate.Year,
                        cancellationToken)
                    .ConfigureAwait(false);

                var order = new PurchaseOrder
                {
                    BusinessId = businessId,
                    SupplierId = request.SupplierId,
                    LocationId = request.LocationId,
                    OrderNumber = number,
                    Status = PurchaseOrderStatus.Draft,
                    OrderDate = orderDate,
                    ExpectedDate = request.ExpectedDate,
                    Currency = business.BaseCurrency,
                    SubtotalAmount = totals.Subtotal,
                    DiscountAmount = totals.Discount,
                    TaxAmount = totals.Tax,
                    TotalAmount = totals.Total,
                    Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                };

                db.PurchaseOrders.Add(order);
                AddOrderLines(db, businessId, order.Id, lines);

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "purchase_order",
                    EntityId = order.Id,
                    Action = "created",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        orderNumber = number,
                        total = order.TotalAmount,
                        supplierId = order.SupplierId,
                        lines = lines.Count,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return order;
            }).ConfigureAwait(false);

            return Results.Created($"/api/v1/purchase-orders/{order.Id}", await ToDetailAsync(db, businessId, order, cancellationToken).ConfigureAwait(false));
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<IResult> GetOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var order = await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return order is null ? NotFoundOrder() : Results.Ok(await ToDetailAsync(db, businessId, order, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> UpdateOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        SavePurchaseOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var order = await db.PurchaseOrders
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return NotFoundOrder();
        }

        if (order.Status != PurchaseOrderStatus.Draft)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PurchaseOrderNotDraft,
                "Only draft purchase orders can be edited.",
                "Ordered quantities are frozen once the order is placed; adjust them with a new order or a return.");
        }

        var prepared = await PrepareOrderAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var lines = prepared.Lines!;
        var totals = TotalsOf(lines);

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await db.PurchaseOrderLines
                .Where(line => line.BusinessId == businessId && line.PurchaseOrderId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            var target = await db.PurchaseOrders
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            target.SupplierId = request.SupplierId;
            target.LocationId = request.LocationId;
            target.ExpectedDate = request.ExpectedDate;
            target.SubtotalAmount = totals.Subtotal;
            target.DiscountAmount = totals.Discount;
            target.TaxAmount = totals.Tax;
            target.TotalAmount = totals.Total;
            target.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

            if (request.OrderDate is { } orderDate)
            {
                target.OrderDate = orderDate;
            }

            AddOrderLines(db, businessId, id, lines);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var updated = await db.PurchaseOrders.AsNoTracking()
            .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(await ToDetailAsync(db, businessId, updated, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> DeleteOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var order = await db.PurchaseOrders
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return NotFoundOrder();
        }

        if (order.Status != PurchaseOrderStatus.Draft)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PurchaseOrderNotDraft,
                "Only draft purchase orders can be deleted.",
                "Cancel the order or post returns for received goods instead.");
        }

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await db.PurchaseOrderLines
                .Where(line => line.BusinessId == businessId && line.PurchaseOrderId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await db.PurchaseOrders
                .Where(candidate => candidate.BusinessId == businessId && candidate.Id == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var order = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var order = await db.PurchaseOrders
                    .FromSql($"SELECT * FROM purchase_orders WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (order is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The purchase order does not exist in this business.",
                        "Check the identifier: purchase orders are scoped to the signed-in business.");
                }

                if (order.Status != PurchaseOrderStatus.Draft)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.PurchaseOrderNotDraft,
                        "Only draft purchase orders can be placed.",
                        "The order was already placed, cancelled or received.");
                }

                order.Status = PurchaseOrderStatus.Ordered;

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "purchase_order",
                    EntityId = order.Id,
                    Action = "ordered",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { orderNumber = order.OrderNumber, total = order.TotalAmount }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return order;
            }).ConfigureAwait(false);

            return Results.Ok(await ToDetailAsync(db, businessId, order, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
    }

    private static async Task<IResult> CancelOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        PurchaseOrderActionRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var reason = request?.Reason?.Trim() ?? string.Empty;

        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem(
                "The cancellation payload is invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"A cancellation reason of 1-{MaxReasonLength} characters is required (audited with the actor and time)."] });
        }

        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var order = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var order = await db.PurchaseOrders
                    .FromSql($"SELECT * FROM purchase_orders WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (order is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The purchase order does not exist in this business.",
                        "Check the identifier: purchase orders are scoped to the signed-in business.");
                }

                if (order.Status is PurchaseOrderStatus.Cancelled)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "The purchase order is already cancelled.",
                        "Cancellation is a one-time transition.");
                }

                if (order.Status is PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "A purchase order with received goods cannot be cancelled.",
                        "Post a return for the received quantity and let the order stay in the history.");
                }

                order.Status = PurchaseOrderStatus.Cancelled;

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "purchase_order",
                    EntityId = order.Id,
                    Action = "cancelled",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { orderNumber = order.OrderNumber, reason }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return order;
            }).ConfigureAwait(false);

            return Results.Ok(await ToDetailAsync(db, businessId, order, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
    }

    // ---------------------------------------------------------------- receipts and returns

    private static async Task<IResult> ReceiveAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        PurchaseReceiptRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        return await PostReceiptAsync(principal, db, id, request, PurchaseReceiptKind.Receipt, http, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ReturnAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        PurchaseReceiptRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        return await PostReceiptAsync(principal, db, id, request, PurchaseReceiptKind.Return, http, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> PostReceiptAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        PurchaseReceiptRequest request,
        PurchaseReceiptKind kind,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var isReturn = kind == PurchaseReceiptKind.Return;
        var label = isReturn ? "return" : "receipt";

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();

        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var payloadHash = RequestFingerprint.Compute(request);

        var existing = await db.PurchaseReceipts.AsNoTracking()
            .FirstOrDefaultAsync(receipt => receipt.BusinessId == businessId && receipt.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different payload.",
                    $"Key '{idempotencyKey}' already recorded a {label}; start a new request instead of changing the payload.");
            }

            return Results.Ok(await ToReceiptDtoAsync(db, businessId, existing, cancellationToken).ConfigureAwait(false));
        }

        if (request?.Lines is null || request.Lines.Count is 0 or > MaxLinesPerRequest)
        {
            return ApiResults.ValidationProblem(
                $"The {label} payload is invalid.",
                new Dictionary<string, string[]> { ["lines"] = [$"A {label} requires 1-{MaxLinesPerRequest} lines."] });
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            return ApiResults.ValidationProblem(
                $"The {label} payload is invalid.",
                new Dictionary<string, string[]> { ["note"] = [$"A note of at most {MaxNoteLength} characters is supported."] });
        }

        var grouped = new Dictionary<Guid, decimal>();

        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0 || decimal.Round(line.Quantity, 6) != line.Quantity)
            {
                return ApiResults.ValidationProblem(
                    $"The {label} payload is invalid.",
                    new Dictionary<string, string[]> { [$"lines[{grouped.Count}]"] = ["A positive quantity with at most 6 decimal places is required per line."] });
            }

            grouped[line.PurchaseOrderLineId] = grouped.TryGetValue(line.PurchaseOrderLineId, out var current)
                ? current + line.Quantity
                : line.Quantity;
        }

        var order = await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return NotFoundOrder();
        }

        if (order.Status is PurchaseOrderStatus.Draft)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PurchaseOrderNotOpen,
                "The purchase order has not been placed yet.",
                "Place the order before recording deliveries against it.");
        }

        if (order.Status is PurchaseOrderStatus.Cancelled)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PurchaseOrderNotOpen,
                "The purchase order is cancelled.",
                "Deliveries cannot be recorded against a cancelled order.");
        }

        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var receipt = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var lockedOrder = await db.PurchaseOrders
                    .FromSql($"SELECT * FROM purchase_orders WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (lockedOrder is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The purchase order does not exist in this business.",
                        "Check the identifier: purchase orders are scoped to the signed-in business.");
                }

                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, lockedOrder.LocationId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainRuleException(
                        StatusCodes.Status403Forbidden,
                        ApiErrorCodes.Forbidden,
                        "The membership cannot access this goods-receipt location.",
                        "Ask a business owner to grant access to the purchase order location.");
                }

                if (lockedOrder.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Cancelled)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.PurchaseOrderNotOpen,
                        "This purchase order cannot take deliveries in its current state.",
                        "Place the order first; cancelled orders stay closed.");
                }

                var orderLines = await db.PurchaseOrderLines
                    .Where(line => line.BusinessId == businessId && line.PurchaseOrderId == id)
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var business = await db.Businesses.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
                    .ConfigureAwait(false);

                if (business is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "The business of this purchase order is not available.",
                        "The receipt cannot be posted without the business currency and calendar.");
                }

                var linesById = orderLines.ToDictionary(line => line.Id);

                foreach (var requested in grouped)
                {
                    if (!linesById.TryGetValue(requested.Key, out var line))
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed,
                            "A line does not belong to this purchase order.",
                            $"Line '{requested.Key}' was not found on order {lockedOrder.OrderNumber}.");
                    }

                    if (!isReturn && line.QuantityReceived + requested.Value > line.QuantityOrdered)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.OverReceipt,
                            "Receiving more than ordered is not allowed.",
                            $"Line {line.LineNo}: ordered {line.QuantityOrdered}, already received {line.QuantityReceived}, requested {requested.Value}.");
                    }

                    if (isReturn && requested.Value > line.QuantityReceived)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.OverReceipt,
                            "Returning more than received is not allowed.",
                            $"Line {line.LineNo}: received {line.QuantityReceived}, requested return {requested.Value}.");
                    }
                }

                var number = await LedgerWriter.AllocateNumberAsync(
                        db,
                        businessId,
                        isReturn ? ReturnSequenceName : ReceiptSequenceName,
                        isReturn ? ReturnSequencePrefix : ReceiptSequencePrefix,
                        LedgerWriter.LocalDate(null, now).Year,
                        cancellationToken)
                    .ConfigureAwait(false);

                var receipt = new PurchaseReceipt
                {
                    BusinessId = businessId,
                    PurchaseOrderId = lockedOrder.Id,
                    LocationId = lockedOrder.LocationId,
                    ReceiptNumber = number,
                    Kind = kind,
                    ReceivedAtUtc = now,
                    ReceivedByUserId = userId,
                    Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                db.PurchaseReceipts.Add(receipt);

                // The receipt capitalises the stock and clears it: the value is not a supplier payable
                // until the bill arrives, and it is not invisible either. Without this entry the
                // quantity moves while the ledger does not (specification D08, finding F16).
                var accounts = await LedgerWriter.ResolveAccountsAsync(
                        db,
                        businessId,
                        [InventoryAccountCode, GrniAccountCode],
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var (code, purpose) in new[]
                {
                    (InventoryAccountCode, "inventory"),
                    (GrniAccountCode, "goods received not invoiced"),
                })
                {
                    if (!accounts.TryGetValue(code, out var account) || !account.IsActive)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.Conflict,
                            "The chart of accounts is not ready for a goods receipt.",
                            $"This business must define an active account '{code}' ({purpose}).");
                    }
                }

                var receiptValue = 0m;
                var lineNo = 1;

                foreach (var requested in grouped)
                {
                    var line = linesById[requested.Key];

                    var receiptLine = new PurchaseReceiptLine
                    {
                        BusinessId = businessId,
                        PurchaseReceiptId = receipt.Id,
                        LineNo = lineNo++,
                        PurchaseOrderLineId = line.Id,
                        VariantId = line.VariantId,
                        ProductNameSnapshot = line.ProductNameSnapshot,
                        SkuSnapshot = line.SkuSnapshot,
                        Quantity = requested.Value,
                        UnitCost = line.UnitCost,
                    };

                    db.PurchaseReceiptLines.Add(receiptLine);

                    await InventoryLedger.ApplyAsync(
                            db,
                            businessId,
                            userId,
                            lockedOrder.LocationId,
                            line.VariantId,
                            isReturn ? MovementType.PurchaseReturn : MovementType.PurchaseReceipt,
                            isReturn ? -requested.Value : requested.Value,
                            line.UnitCost,
                            isReturn ? "purchase_return" : "purchase_receipt",
                            receiptLine.Id,
                            $"{number} / {lockedOrder.OrderNumber}",
                            now,
                                        cancellationToken)
                        .ConfigureAwait(false);

                    // Every receipt moves exactly one line's received quantity; the balance row and the
                    // order line are locked above, so concurrent deliveries serialize and cannot over-receive.
                    line.QuantityReceived = isReturn
                        ? line.QuantityReceived - requested.Value
                        : line.QuantityReceived + requested.Value;

                    receiptValue += LedgerWriter.Round(requested.Value * line.UnitCost);
                }

                if (receiptValue > 0m)
                {
                    var entryLines = isReturn
                        ? new[]
                        {
                            new JournalLineRequest(accounts[GrniAccountCode].Id, receiptValue, 0m, "Goods returned to the supplier", lockedOrder.LocationId),
                            new JournalLineRequest(accounts[InventoryAccountCode].Id, 0m, receiptValue, "Inventory relieved", lockedOrder.LocationId),
                        }
                        : new[]
                        {
                            new JournalLineRequest(accounts[InventoryAccountCode].Id, receiptValue, 0m, "Goods received", lockedOrder.LocationId),
                            new JournalLineRequest(accounts[GrniAccountCode].Id, 0m, receiptValue, "Goods received not invoiced", lockedOrder.LocationId),
                        };

                    await LedgerWriter.PostWithinTransactionAsync(
                            db,
                            businessId,
                            userId,
                            business.BaseCurrency,
                            LedgerWriter.LocalDate(business.TimeZone, now),
                            $"{(isReturn ? "Purchase return" : "Goods receipt")} {number}",
                            JournalSourceType.Purchase,
                            receipt.Id,
                            reversalOfEntryId: null,
                            entryLines,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                ApplyReceivedStatus(lockedOrder, orderLines);

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "purchase_receipt",
                    EntityId = receipt.Id,
                    Action = isReturn ? "returned" : "received",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        receiptNumber = number,
                        orderNumber = lockedOrder.OrderNumber,
                        lines = grouped.Count,
                        orderStatus = lockedOrder.Status.ToString(),
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return receipt;
            }).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/purchase-orders/{id}/receipts/{receipt.Id}",
                await ToReceiptDtoAsync(db, businessId, receipt, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> ListReceiptsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var orderExists = await db.PurchaseOrders.AsNoTracking()
            .AnyAsync(order => order.BusinessId == businessId && order.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (!orderExists)
        {
            return NotFoundOrder();
        }

        var receipts = await db.PurchaseReceipts.AsNoTracking()
            .Where(receipt => receipt.BusinessId == businessId && receipt.PurchaseOrderId == id)
            .OrderBy(receipt => receipt.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dtos = new List<PurchaseReceiptDto>(receipts.Count);

        foreach (var receipt in receipts)
        {
            dtos.Add(await ToReceiptDtoAsync(db, businessId, receipt, cancellationToken).ConfigureAwait(false));
        }

        return Results.Ok(dtos);
    }

    private static async Task<PurchaseReceiptDto> ToReceiptDtoAsync(
        BillingDbContext db,
        Guid businessId,
        PurchaseReceipt receipt,
        CancellationToken cancellationToken)
    {
        var orderNumber = await db.PurchaseOrders.AsNoTracking()
            .Where(order => order.BusinessId == businessId && order.Id == receipt.PurchaseOrderId)
            .Select(order => order.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;

        var lines = await db.PurchaseReceiptLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.PurchaseReceiptId == receipt.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new PurchaseReceiptLineDto(
                line.Id,
                line.LineNo,
                line.PurchaseOrderLineId,
                line.VariantId,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.Quantity,
                line.UnitCost))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PurchaseReceiptDto(
            receipt.Id,
            receipt.ReceiptNumber,
            receipt.Kind.ToString(),
            receipt.PurchaseOrderId,
            orderNumber,
            receipt.LocationId,
            receipt.ReceivedAtUtc,
            receipt.Note,
            lines);
    }

    // ---------------------------------------------------------------- shared helpers

    private static async Task<PreparedOrder> PrepareOrderAsync(
        BillingDbContext db,
        Guid businessId,
        SavePurchaseOrderRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var supplierExists = await db.Suppliers.AsNoTracking()
            .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id == request.SupplierId && candidate.IsActive, cancellationToken)
            .ConfigureAwait(false);

        if (!supplierExists)
        {
            errors["supplierId"] = ["The supplier does not exist or is inactive in this business."];
        }

        var locationExists = await db.BusinessLocations.AsNoTracking()
            .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id == request.LocationId && candidate.IsActive, cancellationToken)
            .ConfigureAwait(false);

        if (!locationExists)
        {
            errors["locationId"] = ["The location does not exist or is inactive in this business."];
        }

        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines)
        {
            errors["lines"] = [$"A purchase order requires 1-{MaxLines} lines."];
        }

        if (request.ExpectedDate is { } expected && expected < request.OrderDate)
        {
            errors["expectedDate"] = ["The expected date cannot precede the order date."];
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            errors["note"] = [$"A note of at most {MaxNoteLength} characters is supported."];
        }

        var lines = new List<PricedOrderLine>();

        if (request.Lines is { Count: > 0 })
        {
            var lineNo = 1;

            foreach (var line in request.Lines)
            {
                var key = $"lines[{lineNo - 1}]";

                if (line.Quantity <= 0 || line.Quantity > 1_000_000m || decimal.Round(line.Quantity, 6) != line.Quantity)
                {
                    errors[$"{key}.quantity"] = ["A positive quantity with at most 6 decimal places is required."];
                }

                if (line.UnitCost < 0 || LedgerWriter.Round(line.UnitCost) != line.UnitCost)
                {
                    errors[$"{key}.unitCost"] = ["A non-negative unit cost of at most 4 decimal places is required."];
                }

                var gross = LedgerWriter.Round(line.Quantity * line.UnitCost);

                if (line.DiscountAmount < 0 || line.DiscountAmount > gross)
                {
                    errors[$"{key}.discountAmount"] = ["A discount between zero and the line gross amount is required."];
                }

                if (line.TaxRate < 0 || line.TaxRate > 1)
                {
                    errors[$"{key}.taxRate"] = ["The tax rate is a fraction between 0 and 1."];
                }

                var variant = await db.ProductVariants.AsNoTracking()
                    .Where(candidate => candidate.BusinessId == businessId && candidate.Id == line.VariantId)
                    .Select(candidate => new { candidate.Id, candidate.ProductId, candidate.Sku, candidate.IsActive })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (variant is null || !variant.IsActive)
                {
                    errors[$"{key}.variantId"] = ["The variant does not exist or is inactive in this business."];
                    lines.Add(new PricedOrderLine(line.VariantId, string.Empty, null, line.Quantity, LedgerWriter.Round(line.UnitCost), Math.Max(0, line.DiscountAmount), line.TaxRate, 0m, 0m));
                    lineNo++;
                    continue;
                }

                var product = await db.Products.AsNoTracking()
                    .Where(candidate => candidate.BusinessId == businessId && candidate.Id == variant.ProductId)
                    .Select(candidate => new { candidate.Name, candidate.IsActive })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (product is null || !product.IsActive)
                {
                    errors[$"{key}.variantId"] = ["The product of this variant is inactive."];
                    lines.Add(new PricedOrderLine(line.VariantId, string.Empty, variant.Sku, line.Quantity, LedgerWriter.Round(line.UnitCost), Math.Max(0, line.DiscountAmount), line.TaxRate, 0m, 0m));
                    lineNo++;
                    continue;
                }

                var net = LedgerWriter.Round(gross - Math.Max(0, line.DiscountAmount));
                var tax = LedgerWriter.Round(net * line.TaxRate);

                lines.Add(new PricedOrderLine(
                    line.VariantId,
                    product.Name,
                    variant.Sku,
                    line.Quantity,
                    LedgerWriter.Round(line.UnitCost),
                    Math.Max(0, line.DiscountAmount),
                    line.TaxRate,
                    tax,
                    LedgerWriter.Round(net + tax)));

                lineNo++;
            }
        }

        if (errors.Count > 0)
        {
            return new PreparedOrder(ApiResults.ValidationProblem("The purchase order payload is invalid.", errors), null);
        }

        return new PreparedOrder(null, lines);
    }

    private static void AddOrderLines(BillingDbContext db, Guid businessId, Guid orderId, IReadOnlyList<PricedOrderLine> lines)
    {
        var lineNo = 1;

        foreach (var line in lines)
        {
            db.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                BusinessId = businessId,
                PurchaseOrderId = orderId,
                LineNo = lineNo++,
                VariantId = line.VariantId,
                ProductNameSnapshot = line.ProductName,
                SkuSnapshot = line.Sku,
                QuantityOrdered = line.Quantity,
                QuantityReceived = 0,
                UnitCost = line.UnitCost,
                DiscountAmount = line.DiscountAmount,
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                LineTotal = line.LineTotal,
            });
        }
    }

    private static OrderTotals TotalsOf(IReadOnlyList<PricedOrderLine> lines)
    {
        var subtotal = LedgerWriter.Round(lines.Sum(line => LedgerWriter.Round(line.Quantity * line.UnitCost)));
        var discount = LedgerWriter.Round(lines.Sum(line => line.DiscountAmount));
        var tax = LedgerWriter.Round(lines.Sum(line => line.TaxAmount));
        var total = LedgerWriter.Round(lines.Sum(line => line.LineTotal));

        return new OrderTotals(subtotal, discount, tax, total);
    }

    private static void ApplyReceivedStatus(PurchaseOrder order, List<PurchaseOrderLine> orderLines)
    {
        if (orderLines.Count == 0)
        {
            return;
        }

        if (orderLines.All(line => line.QuantityReceived >= line.QuantityOrdered))
        {
            order.Status = PurchaseOrderStatus.Received;
        }
        else if (orderLines.Any(line => line.QuantityReceived > 0))
        {
            order.Status = PurchaseOrderStatus.PartiallyReceived;
        }
        else
        {
            order.Status = PurchaseOrderStatus.Ordered;
        }
    }

    private static async Task<PurchaseOrderDetailDto> ToDetailAsync(
        BillingDbContext db,
        Guid businessId,
        PurchaseOrder order,
        CancellationToken cancellationToken)
    {
        var lines = await db.PurchaseOrderLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.PurchaseOrderId == order.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new PurchaseOrderLineDto(
                line.Id,
                line.LineNo,
                line.VariantId,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.QuantityOrdered,
                line.QuantityReceived,
                line.UnitCost,
                line.DiscountAmount,
                line.TaxRate,
                line.TaxAmount,
                line.LineTotal))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var supplierName = await db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.BusinessId == businessId && supplier.Id == order.SupplierId)
            .Select(supplier => supplier.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PurchaseOrderDetailDto(
            ToSummary(
                new OrderRow(
                    order.Id,
                    order.OrderNumber,
                    order.Status,
                    order.SupplierId,
                    order.LocationId,
                    order.OrderDate,
                    order.ExpectedDate,
                    order.Currency,
                    order.SubtotalAmount,
                    order.DiscountAmount,
                    order.TaxAmount,
                    order.TotalAmount,
                    order.Note,
                    order.CreatedAtUtc),
                supplierName),
            lines);
    }

    private static PurchaseOrderSummaryDto ToSummary(OrderRow row, string? supplierName)
        => new(
            row.Id,
            row.OrderNumber,
            EnumBridge.To<PurchaseOrderState>(row.Status),
            row.SupplierId,
            supplierName,
            row.LocationId,
            row.OrderDate,
            row.ExpectedDate,
            row.Currency,
            row.SubtotalAmount,
            row.DiscountAmount,
            row.TaxAmount,
            row.TotalAmount,
            row.Note,
            row.CreatedAtUtc);

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult NotFoundOrder()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The purchase order does not exist in this business.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_purchase_receipts_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same key committed first.",
                    "Retry the same payload: the stored document will be returned instead of moving stock twice."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The purchasing write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The purchasing write violates an integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The purchasing write references a record outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The purchasing operation failed."),
        };

    // ---------------------------------------------------------------- local shapes

    private sealed record PricedOrderLine(
        Guid VariantId,
        string ProductName,
        string? Sku,
        decimal Quantity,
        decimal UnitCost,
        decimal DiscountAmount,
        decimal TaxRate,
        decimal TaxAmount,
        decimal LineTotal);

    private sealed record OrderTotals(decimal Subtotal, decimal Discount, decimal Tax, decimal Total);

    private sealed record PreparedOrder(IResult? Error, IReadOnlyList<PricedOrderLine>? Lines);

    private sealed record OrderRow(
        Guid Id,
        string OrderNumber,
        PurchaseOrderStatus Status,
        Guid SupplierId,
        Guid LocationId,
        DateOnly OrderDate,
        DateOnly? ExpectedDate,
        string Currency,
        decimal SubtotalAmount,
        decimal DiscountAmount,
        decimal TaxAmount,
        decimal TotalAmount,
        string? Note,
        DateTimeOffset CreatedAtUtc);
}
