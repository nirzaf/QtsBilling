using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Features.Invoices;
using QtsWebApp.Infrastructure;
using QtsModels.ValueObjects;

namespace QtsWebApp.Features.Orders;

/// <summary>Inventory reservation and delivery operations share the stock balance lock used by POS.</summary>
internal static class FulfillmentService
{
    public static async Task<decimal> GetActiveReservedQuantityAsync(
        BillingDbContext db,
        Guid businessId,
        Guid locationId,
        Guid variantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var quantity = await (from line in db.InventoryReservationLines.AsNoTracking()
                              join reservation in db.InventoryReservations.AsNoTracking()
                                  on new { line.ReservationId, line.BusinessId } equals new { ReservationId = reservation.Id, reservation.BusinessId }
                              where line.BusinessId == businessId
                                    && line.VariantId == variantId
                                    && reservation.LocationId == locationId
                                    && reservation.Status == InventoryReservationStatus.Active
                                    && reservation.ExpiresAtUtc > now
                              select line.QuantityReserved - line.QuantityConsumed - line.QuantityReleased)
            .SumAsync(value => (decimal?)value, cancellationToken)
            .ConfigureAwait(false);
        return quantity ?? 0m;
    }

    public static async Task ReleaseOrderReservationsAsync(
        BillingDbContext db,
        Guid businessId,
        Guid orderId,
        Guid actorId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reservations = await db.InventoryReservations
            .Where(row => row.BusinessId == businessId && row.OrderId == orderId && row.Status == InventoryReservationStatus.Active)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reservations.Count == 0) return;
        var ids = reservations.Select(row => row.Id).ToArray();
        var lines = await db.InventoryReservationLines
            .Where(row => row.BusinessId == businessId && ids.Contains(row.ReservationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var reservation in reservations)
        {
            var ownLines = lines.Where(line => line.ReservationId == reservation.Id).ToArray();
            foreach (var line in ownLines)
            {
                var remaining = line.QuantityReserved - line.QuantityConsumed - line.QuantityReleased;
                if (remaining > 0m) line.QuantityReleased += remaining;
            }
            reservation.Status = reservation.ExpiresAtUtc <= now
                ? InventoryReservationStatus.Expired
                : ownLines.Any(line => line.QuantityConsumed > 0m) && ownLines.All(line => line.QuantityReleased == 0m)
                    ? InventoryReservationStatus.Fulfilled
                    : InventoryReservationStatus.Released;
            reservation.ClosedAtUtc = now;
            reservation.CloseReason = reason;
            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = businessId,
                UserId = actorId,
                EntityType = "inventory_reservation",
                EntityId = reservation.Id,
                Action = reservation.Status == InventoryReservationStatus.Expired ? "expired" : "released",
                OccurredAtUtc = now,
                Summary = JsonSerializer.Serialize(new { orderId, reason, lineCount = ownLines.Length }),
            });
        }
    }
}

public static class FulfillmentEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxLines = 100;
    private static readonly TimeSpan MaxReservationDuration = TimeSpan.FromDays(30);

    public static IEndpointRouteBuilder MapFulfillmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/sales-orders/{id:guid}/reservations", ReserveAsync)
            .WithTags("Orders")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage, Permissions.InventoryDispatch);
        app.MapPost("/api/v1/sales-orders/{id:guid}/reservations/release", ReleaseAsync)
            .WithTags("Orders")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage, Permissions.InventoryDispatch);
        app.MapPost("/api/v1/sales-orders/{id:guid}/deliveries", DeliverAsync)
            .WithTags("Orders")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage, Permissions.InventoryDispatch);
        app.MapPost("/api/v1/sales-orders/{id:guid}/deliveries/{deliveryId:guid}/invoices", CreateInvoiceFromDeliveryAsync)
            .WithTags("Orders", "Invoices")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage, Permissions.InvoicesIssue);
        app.MapGet("/api/v1/sales-orders/{id:guid}/deliveries", ListDeliveriesAsync)
            .WithTags("Orders")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage);
        return app;
    }

    private static async Task<IResult> CreateInvoiceFromDeliveryAsync(
        Guid id,
        Guid deliveryId,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CreateDeliveryInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        if (!ValidIdempotencyKey(http.Request.Headers[IdempotencyHeader].ToString(), out var key)) return MissingIdempotencyKey();
        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines
            || request.Lines.Any(line => line.DeliveryLineId == Guid.Empty || line.Quantity <= 0m || decimal.Round(line.Quantity, 6) != line.Quantity)
            || request.Lines.Select(line => line.DeliveryLineId).Distinct().Count() != request.Lines.Count)
            return InvalidLines("Provide unique delivery lines with positive quantities of at most six decimal places.");
        if (request.Note?.Trim().Length > 2_000) return InvalidNote();
        if (request.DueDate is { } dueDate && dueDate < request.IssueDate) return InvalidDates();

        var fingerprint = RequestFingerprint.Compute(new { OrderId = id, DeliveryId = deliveryId, request.IssueDate, request.DueDate, request.Note, request.Lines });
        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();
        var replayed = false;
        try
        {
            var invoice = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LockIdempotencyKeyAsync(db, businessId, "delivery-invoice", key, cancellationToken).ConfigureAwait(false);
                var replay = await db.Invoices.SingleOrDefaultAsync(row => row.BusinessId == businessId && row.SourceRequestKey == key, cancellationToken).ConfigureAwait(false);
                if (replay is not null)
                {
                    if (replay.SourceOrderId != id || replay.SourcePayloadHash != fingerprint) throw IdempotencyReuseException();
                    replayed = true;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return replay;
                }

                var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                if (order is null) throw NotFoundOrderException();
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) throw ForbiddenLocationException();
                var delivery = await db.SalesOrderDeliveries.FromSql($"SELECT * FROM sales_order_deliveries WHERE business_id = {businessId} AND id = {deliveryId} AND order_id = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (delivery is null) throw NotFoundDeliveryException();
                if (request.IssueDate < delivery.DeliveryDate || request.IssueDate > LedgerWriter.LocalDate(
                    await db.Businesses.AsNoTracking().Where(row => row.Id == businessId).Select(row => row.TimeZone).SingleAsync(cancellationToken).ConfigureAwait(false), now))
                    throw InvalidInvoiceDatesException();

                var requestedIds = request.Lines.Select(line => line.DeliveryLineId).Order().ToArray();
                var deliveryLines = await db.SalesOrderDeliveryLines
                    .FromSql($"SELECT * FROM sales_order_delivery_lines WHERE business_id = {businessId} AND delivery_id = {deliveryId} AND id = ANY({requestedIds}) ORDER BY id FOR UPDATE")
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (deliveryLines.Count != requestedIds.Length) throw InvalidLinesException("An invoice line does not belong to this delivery.");
                var requestedById = request.Lines.ToDictionary(line => line.DeliveryLineId);
                var lineIds = deliveryLines.Select(line => line.Id).ToArray();
                var existingAllocations = await db.DeliveryInvoiceAllocations.AsNoTracking()
                    .Where(row => row.BusinessId == businessId && lineIds.Contains(row.DeliveryLineId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var allocatedByDeliveryLine = existingAllocations.GroupBy(row => row.DeliveryLineId)
                    .ToDictionary(group => group.Key, group => new { Quantity = group.Sum(row => row.Quantity), CostValue = group.Sum(row => row.CostValue) });

                var orderLineIds = deliveryLines.Select(line => line.OrderLineId).Distinct().Order().ToArray();
                var orderLines = await db.SalesOrderLines
                    .FromSql($"SELECT * FROM sales_order_lines WHERE business_id = {businessId} AND order_id = {id} AND id = ANY({orderLineIds}) ORDER BY id FOR UPDATE")
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (orderLines.Count != orderLineIds.Length) throw InvalidLinesException("A delivery refers to an unavailable order line.");
                var orderLineById = orderLines.ToDictionary(line => line.Id);
                var priorOrderAllocations = await (from allocation in db.DeliveryInvoiceAllocations.AsNoTracking()
                                                   join deliveredLine in db.SalesOrderDeliveryLines.AsNoTracking()
                                                       on new { allocation.DeliveryLineId, allocation.BusinessId } equals new { DeliveryLineId = deliveredLine.Id, deliveredLine.BusinessId }
                                                   where allocation.BusinessId == businessId && orderLineIds.Contains(deliveredLine.OrderLineId)
                                                   select new { deliveredLine.OrderLineId, allocation.Quantity })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var invoicedByOrderLine = priorOrderAllocations.GroupBy(row => row.OrderLineId)
                    .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));

                var snapshotBusiness = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The business is unavailable.", "Verify the active business membership.");
                var snapshotCustomer = order.CustomerId is { } sourceCustomerId
                    ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == sourceCustomerId, cancellationToken).ConfigureAwait(false)
                    : null;
                var snapshotLocation = await db.BusinessLocations.AsNoTracking().FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == order.LocationId, cancellationToken).ConfigureAwait(false);
                var invoice = new Invoice
                {
                    BusinessId = businessId,
                    RecognitionMode = InvoiceRecognitionMode.Delivery.ToString(),
                    LocationId = order.LocationId,
                    CustomerId = order.CustomerId,
                    SourceOrderId = order.Id,
                    SourceRequestKey = key,
                    SourcePayloadHash = fingerprint,
                    InvoiceNumber = string.Empty,
                    Status = InvoiceStatus.Draft,
                    IssueDate = request.IssueDate,
                    DueDate = request.DueDate,
                    Currency = order.Currency,
                    BusinessNameSnapshot = snapshotBusiness.Name,
                    BusinessLegalNameSnapshot = snapshotBusiness.LegalName,
                    LocationAddressSnapshot = snapshotLocation?.Address,
                    CustomerNameSnapshot = snapshotCustomer?.Name,
                    CustomerAddressSnapshot = snapshotCustomer?.Address,
                    CustomerPhoneSnapshot = snapshotCustomer?.Phone,
                    CustomerEmailSnapshot = snapshotCustomer?.Email,
                    PaymentTermsDaysSnapshot = snapshotCustomer?.PaymentTermsDays,
                    AmountPaid = 0m,
                    CreditedAmount = 0m,
                    Note = NormalizeNote(request.Note),
                };
                var invoiceLines = new List<InvoiceLine>(deliveryLines.Count);
                var pendingAllocations = new List<(SalesOrderDeliveryLine DeliveryLine, InvoiceLine InvoiceLine, decimal Quantity, decimal CostValue)>();
                foreach (var deliveryLine in deliveryLines.OrderBy(row => orderLineById[row.OrderLineId].LineNo))
                {
                    var requestedQuantity = requestedById[deliveryLine.Id].Quantity;
                    var already = allocatedByDeliveryLine.GetValueOrDefault(deliveryLine.Id, new { Quantity = 0m, CostValue = 0m });
                    if (requestedQuantity > deliveryLine.Quantity - already.Quantity) throw DeliveryInvoiceQuantityConflictException();
                    var orderLine = orderLineById[deliveryLine.OrderLineId];
                    var priorQuantity = invoicedByOrderLine.GetValueOrDefault(orderLine.Id);
                    var cumulativeQuantity = priorQuantity + requestedQuantity;
                    if (cumulativeQuantity > orderLine.QuantityFulfilled) throw DeliveryInvoiceQuantityConflictException();
                    var before = SliceOrderLine(orderLine, priorQuantity);
                    var after = SliceOrderLine(orderLine, cumulativeQuantity);
                    var gross = LedgerWriter.Round(requestedQuantity * orderLine.UnitPrice);
                    var discount = after.DiscountAmount - before.DiscountAmount;
                    var revenueBase = after.RevenueBaseAmount - before.RevenueBaseAmount;
                    var tax = after.TaxAmount - before.TaxAmount;
                    var payable = after.PayableAmount - before.PayableAmount;
                    var newCumulativeCost = already.Quantity + requestedQuantity == deliveryLine.Quantity
                        ? deliveryLine.CostValue
                        : LedgerWriter.Round(deliveryLine.CostValue * (already.Quantity + requestedQuantity) / deliveryLine.Quantity);
                    var costSlice = newCumulativeCost - already.CostValue;
                    var invoiceLine = new InvoiceLine
                    {
                        BusinessId = businessId,
                        InvoiceId = invoice.Id,
                        LineNo = invoiceLines.Count + 1,
                        VariantId = orderLine.VariantId,
                        SourceDeliveryLineId = deliveryLine.Id,
                        ProductNameSnapshot = orderLine.Description,
                        SkuSnapshot = orderLine.SkuSnapshot,
                        Quantity = requestedQuantity,
                        UnitPrice = orderLine.UnitPrice,
                        DiscountAmount = discount,
                        RevenueBaseAmount = revenueBase,
                        TaxMode = orderLine.TaxMode,
                        TaxRate = orderLine.TaxRate,
                        TaxAmount = tax,
                        LineTotal = payable,
                        UnitCostSnapshot = requestedQuantity == deliveryLine.Quantity - already.Quantity
                            ? LedgerWriter.Round(costSlice / requestedQuantity)
                            : LedgerWriter.Round(deliveryLine.UnitCost),
                        TrackStockSnapshot = deliveryLine.TrackStockSnapshot,
                    };
                    invoiceLines.Add(invoiceLine);
                    pendingAllocations.Add((deliveryLine, invoiceLine, requestedQuantity, costSlice));
                    invoicedByOrderLine[orderLine.Id] = cumulativeQuantity;
                    allocatedByDeliveryLine[deliveryLine.Id] = new { Quantity = already.Quantity + requestedQuantity, CostValue = already.CostValue + costSlice };
                }

                invoice.SubtotalAmount = LedgerWriter.Round(invoiceLines.Sum(line => LedgerWriter.Round(line.Quantity * line.UnitPrice)));
                invoice.DiscountAmount = LedgerWriter.Round(invoiceLines.Sum(line => line.DiscountAmount));
                invoice.TaxAmount = LedgerWriter.Round(invoiceLines.Sum(line => line.TaxAmount));
                invoice.TotalAmount = LedgerWriter.Round(invoiceLines.Sum(line => line.LineTotal));
                invoice.BalanceDue = invoice.TotalAmount;
                if (invoice.CustomerId is { } customerId)
                    await CustomerCreditLimitGuard.EnsureWithinLimitAsync(db, businessId, customerId, invoice.Currency, invoice.BalanceDue, cancellationToken).ConfigureAwait(false);

                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, ["1100", "2100", "4000", "1250", "5000"], cancellationToken).ConfigureAwait(false);
                if (!IsAccount(accounts, "1100", AccountType.Asset) || !IsAccount(accounts, "4000", AccountType.Revenue)
                    || invoice.TaxAmount > 0m && !IsAccount(accounts, "2100", AccountType.Liability)
                    || pendingAllocations.Sum(item => item.CostValue) > 0m && (!IsAccount(accounts, "1250", AccountType.Asset) || !IsAccount(accounts, "5000", AccountType.Expense)))
                    throw MissingInvoiceAccountsException();

                invoice.InvoiceNumber = await LedgerWriter.AllocateNumberAsync(db, businessId, "invoice", "INV-", request.IssueDate.Year, cancellationToken).ConfigureAwait(false);
                db.Invoices.Add(invoice);
                db.InvoiceLines.AddRange(invoiceLines);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var journalLines = new List<JournalLineRequest>
                {
                    new(accounts["1100"].Id, invoice.TotalAmount, 0m, $"Invoice {invoice.InvoiceNumber}", order.LocationId),
                    new(accounts["4000"].Id, 0m, LedgerWriter.Round(invoiceLines.Sum(line => line.RevenueBaseAmount)), $"Invoice {invoice.InvoiceNumber}", order.LocationId),
                };
                if (invoice.TaxAmount > 0m) journalLines.Add(new JournalLineRequest(accounts["2100"].Id, 0m, invoice.TaxAmount, $"Invoice {invoice.InvoiceNumber} tax", order.LocationId));
                var costToClear = LedgerWriter.Round(pendingAllocations.Sum(item => item.CostValue));
                if (costToClear > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["5000"].Id, costToClear, 0m, $"Cost of goods sold {invoice.InvoiceNumber}", order.LocationId));
                    journalLines.Add(new JournalLineRequest(accounts["1250"].Id, 0m, costToClear, $"Clear delivered goods {invoice.InvoiceNumber}", order.LocationId));
                }
                var entry = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, order.Currency, request.IssueDate,
                    $"Invoice {invoice.InvoiceNumber} from delivery {delivery.DeliveryNumber}", JournalSourceType.Invoice, invoice.Id, null, journalLines, cancellationToken).ConfigureAwait(false);
                invoice.IssuedAtUtc = now;
                invoice.Status = InvoiceStatus.Issued;
                foreach (var allocation in pendingAllocations)
                {
                    db.DeliveryInvoiceAllocations.Add(new DeliveryInvoiceAllocation
                    {
                        BusinessId = businessId,
                        DeliveryLineId = allocation.DeliveryLine.Id,
                        InvoiceLineId = allocation.InvoiceLine.Id,
                        JournalEntryId = entry.Id,
                        Quantity = allocation.Quantity,
                        CostValue = allocation.CostValue,
                    });
                }
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "invoice",
                    EntityId = invoice.Id,
                    Action = "issued_from_delivery",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { invoice.InvoiceNumber, deliveryId, orderId = id, lineCount = invoiceLines.Count, costToClear }),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return invoice;
            }).ConfigureAwait(false);
            var detail = await InvoiceEndpoints.ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false);
            return replayed ? Results.Ok(detail) : Results.Created($"/api/v1/invoices/{invoice.Id}", detail);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres) { return MapPostgresException(postgres); }
    }

    private static async Task<IResult> ReserveAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        ReserveSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        if (!ValidIdempotencyKey(http.Request.Headers[IdempotencyHeader].ToString(), out var key)) return MissingIdempotencyKey();
        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now + MaxReservationDuration)
            return ApiResults.ValidationProblem("Reservation expiry must be in the next 30 days.", new Dictionary<string, string[]> { [nameof(request.ExpiresAtUtc)] = ["Choose an expiry after now and no more than 30 days away."] });
        var fingerprint = RequestFingerprint.Compute(new { OrderId = id, request.ExpectedOrderVersion, request.ExpiresAtUtc });
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LockIdempotencyKeyAsync(db, businessId, "order-reservation", key, cancellationToken).ConfigureAwait(false);
                var existing = await db.InventoryReservations.SingleOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.PayloadHash, fingerprint, StringComparison.Ordinal)) return IdempotencyConflict();
                    var replay = await ToReservationDtoAsync(db, existing, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Results.Ok(replay);
                }

                var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                if (order is null) return NotFoundOrder();
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
                if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyFulfilled)) return OrderOpenConflict();
                if (order.Version != request.ExpectedOrderVersion) return StaleVersion();

                var orderLines = await db.SalesOrderLines
                    .Where(line => line.BusinessId == businessId && line.OrderId == id)
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (orderLines.Count == 0) return InvalidLines("The order has no reservable lines.");
                var variants = await GetStockVariantsAsync(db, businessId, orderLines, cancellationToken).ConfigureAwait(false);
                if (orderLines.Any(line => line.VariantId is { } variantId
                    && (!variants.TryGetValue(variantId, out var variant) || !variant.VariantIsActive || !variant.ProductIsActive)))
                    return InvalidLines("Every product and variant on the order must still be active before reserving stock.");
                var stockLines = orderLines.Where(line => line.VariantId is { } variantId && variants.TryGetValue(variantId, out var row) && row.TrackStock)
                    .ToArray();
                if (stockLines.Length == 0) return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This order has no stock-managed quantities to reserve.");

                foreach (var variantId in stockLines.Select(line => line.VariantId!.Value).Distinct().Order())
                {
                    await InventoryLedger.LockBalanceAsync(db, businessId, order.LocationId, variantId, now, cancellationToken).ConfigureAwait(false);
                }
                await ExpireOrderReservationsAsync(db, businessId, id, userId, now, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var reservation = new InventoryReservation
                {
                    BusinessId = businessId,
                    OrderId = order.Id,
                    LocationId = order.LocationId,
                    Status = InventoryReservationStatus.Active,
                    ExpiresAtUtc = request.ExpiresAtUtc,
                    CreatedByUserId = userId,
                    IdempotencyKey = key,
                    PayloadHash = fingerprint,
                };
                var reservationLines = new List<InventoryReservationLine>();
                var reservedByVariant = new Dictionary<Guid, decimal>();
                var activeByLine = await GetReservedByOrderLineAsync(db, businessId, order.LocationId, stockLines.Select(line => line.Id).ToArray(), now, cancellationToken).ConfigureAwait(false);
                foreach (var line in stockLines)
                {
                    var outstanding = line.QuantityOrdered - line.QuantityFulfilled - line.QuantityCancelled;
                    var alreadyReserved = activeByLine.GetValueOrDefault(line.Id);
                    var additional = outstanding - alreadyReserved;
                    if (additional <= 0m) continue;
                    var variantId = line.VariantId!.Value;
                    reservedByVariant[variantId] = reservedByVariant.GetValueOrDefault(variantId) + additional;
                    reservationLines.Add(new InventoryReservationLine
                    {
                        BusinessId = businessId,
                        ReservationId = reservation.Id,
                        OrderLineId = line.Id,
                        VariantId = variantId,
                        QuantityReserved = additional,
                    });
                }
                foreach (var (variantId, wanted) in reservedByVariant)
                {
                    var balance = await db.InventoryBalances.AsNoTracking().SingleAsync(row => row.BusinessId == businessId && row.LocationId == order.LocationId && row.VariantId == variantId, cancellationToken).ConfigureAwait(false);
                    var reserved = await FulfillmentService.GetActiveReservedQuantityAsync(db, businessId, order.LocationId, variantId, now, cancellationToken).ConfigureAwait(false);
                    var available = balance.OnHand - reserved;
                    if (wanted > available)
                        return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.InsufficientStock, "Not enough available stock to reserve the confirmed order.", $"Available stock is {available}; the order needs {wanted}.");
                }
                if (reservationLines.Count == 0) return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "All stock-managed order quantities are already reserved or fulfilled.");

                db.InventoryReservations.Add(reservation);
                db.InventoryReservationLines.AddRange(reservationLines);
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "inventory_reservation",
                    EntityId = reservation.Id,
                    Action = "reserved",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { orderId = id, reservation.ExpiresAtUtc, lineCount = reservationLines.Count }),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/v1/sales-orders/{id}/reservations/{reservation.Id}", await ToReservationDtoAsync(db, reservation, cancellationToken).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres) { return MapPostgresException(postgres); }
    }

    private static async Task<IResult> ReleaseAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        ReleaseSalesOrderReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) return InvalidReason();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                if (order is null) return NotFoundOrder();
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
                if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyFulfilled or SalesOrderStatus.Closed)) return OrderOpenConflict();
                if (order.Version != request.ExpectedOrderVersion) return StaleVersion();
                var now = DateTimeOffset.UtcNow;
                await FulfillmentService.ReleaseOrderReservationsAsync(db, businessId, id, userId, reason, now, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "sales_order",
                    EntityId = id,
                    Action = "reservations_released",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { reason }),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var latest = await db.InventoryReservations.Where(row => row.BusinessId == businessId && row.OrderId == id)
                    .OrderByDescending(row => row.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return latest is null ? Results.NoContent() : Results.Ok(await ToReservationDtoAsync(db, latest, cancellationToken).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres) { return MapPostgresException(postgres); }
    }

    private static async Task<IResult> DeliverAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        DeliverSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        if (!ValidIdempotencyKey(http.Request.Headers[IdempotencyHeader].ToString(), out var key)) return MissingIdempotencyKey();
        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines || request.Lines.Any(line => line.OrderLineId == Guid.Empty || line.Quantity <= 0m)
            || request.Lines.Select(line => line.OrderLineId).Distinct().Count() != request.Lines.Count)
            return InvalidLines("Provide unique order lines with positive delivery quantities.");
        if (!ValidNote(request.Note)) return InvalidNote();
        var fingerprint = RequestFingerprint.Compute(new { OrderId = id, request.ExpectedOrderVersion, request.DeliveryDate, request.Note, request.Lines });
        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LockIdempotencyKeyAsync(db, businessId, "order-delivery", key, cancellationToken).ConfigureAwait(false);
                var existing = await db.SalesOrderDeliveries.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.PayloadHash, fingerprint, StringComparison.Ordinal)) return IdempotencyConflict();
                    var replay = await ToDeliveryDtoAsync(db, existing, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Results.Ok(replay);
                }

                var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                if (order is null) return NotFoundOrder();
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
                if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyFulfilled)) return OrderOpenConflict();
                if (order.Version != request.ExpectedOrderVersion) return StaleVersion();
                if (request.DeliveryDate < order.OrderDate) return InvalidDates();
                var businessTimeZone = await db.Businesses.AsNoTracking().Where(row => row.Id == businessId).Select(row => row.TimeZone).SingleAsync(cancellationToken).ConfigureAwait(false);
                if (request.DeliveryDate > LedgerWriter.LocalDate(businessTimeZone, now)) return InvalidDates();

                var orderLines = await db.SalesOrderLines.FromSql($"SELECT * FROM sales_order_lines WHERE business_id = {businessId} AND order_id = {id} FOR UPDATE")
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                var lineById = orderLines.ToDictionary(line => line.Id);
                if (request.Lines.Any(line => !lineById.ContainsKey(line.OrderLineId))) return InvalidLines("A delivery line does not belong to this order.");
                var selected = request.Lines.Select(item => (Request: item, Line: lineById[item.OrderLineId])).ToArray();
                foreach (var item in selected)
                {
                    var outstanding = item.Line.QuantityOrdered - item.Line.QuantityFulfilled - item.Line.QuantityCancelled;
                    if (item.Request.Quantity > outstanding) return DeliveryQuantityConflict();
                }

                var variants = await GetStockVariantsAsync(db, businessId, selected.Select(item => item.Line).ToArray(), cancellationToken).ConfigureAwait(false);
                if (selected.Any(item => item.Line.VariantId is { } variantId
                    && (!variants.TryGetValue(variantId, out var variant) || !variant.VariantIsActive || !variant.ProductIsActive)))
                    return InvalidLines("Every product and variant on the order must still be active before delivery.");
                var trackedLines = selected.Where(item => item.Line.VariantId is { } variantId && variants.TryGetValue(variantId, out var row) && row.TrackStock).ToArray();
                foreach (var variantId in trackedLines.Select(item => item.Line.VariantId!.Value).Distinct().Order())
                {
                    await InventoryLedger.LockBalanceAsync(db, businessId, order.LocationId, variantId, now, cancellationToken).ConfigureAwait(false);
                }

                var trackedOrderLineIds = trackedLines.Select(item => item.Line.Id).ToArray();
                var reservationLines = trackedOrderLineIds.Length == 0
                    ? []
                    : await db.InventoryReservationLines
                        .FromSql($"""SELECT l.* FROM inventory_reservation_lines l JOIN inventory_reservations r ON r.id = l.reservation_id AND r.business_id = l.business_id WHERE l.business_id = {businessId} AND r.order_id = {id} AND r.status = 'Active' AND r.expires_at_utc > {now} AND l.order_line_id = ANY({trackedOrderLineIds}) ORDER BY r.created_at_utc, l.id FOR UPDATE OF l""")
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                var reservationByLine = reservationLines.GroupBy(line => line.OrderLineId).ToDictionary(group => group.Key, group => group.ToList());
                foreach (var item in trackedLines)
                {
                    var ownReservationLines = reservationByLine.GetValueOrDefault(item.Line.Id, []);
                    if (ownReservationLines.Count != 1) return ReservationQuantityConflict();
                    var reserved = ownReservationLines.Sum(line => line.QuantityReserved - line.QuantityConsumed - line.QuantityReleased);
                    if (item.Request.Quantity > reserved) return ReservationQuantityConflict();
                }

                var delivery = new SalesOrderDelivery
                {
                    BusinessId = businessId,
                    OrderId = id,
                    LocationId = order.LocationId,
                    DeliveryNumber = await OrderEndpoints.AllocateDocumentNumberAsync(db, businessId, order.LocationId, "delivery", "DN-", request.DeliveryDate.Year, cancellationToken).ConfigureAwait(false),
                    DeliveryDate = request.DeliveryDate,
                    CreatedByUserId = userId,
                    IdempotencyKey = key,
                    PayloadHash = fingerprint,
                    Note = NormalizeNote(request.Note),
                };
                var deliveryLines = new List<SalesOrderDeliveryLine>();
                foreach (var item in selected)
                {
                    var isTracked = item.Line.VariantId is { } variantId && variants.TryGetValue(variantId, out var stockVariant) && stockVariant.TrackStock;
                    var remainingToConsume = isTracked ? item.Request.Quantity : 0m;
                    if (isTracked)
                    {
                        foreach (var reservationLine in reservationByLine[item.Line.Id])
                        {
                            var available = reservationLine.QuantityReserved - reservationLine.QuantityConsumed - reservationLine.QuantityReleased;
                            var take = Math.Min(available, remainingToConsume);
                            if (take <= 0m) continue;
                            reservationLine.QuantityConsumed += take;
                            remainingToConsume -= take;
                            if (remainingToConsume == 0m) break;
                        }
                        if (remainingToConsume > 0m) return ReservationQuantityConflict();
                    }

                    var deliveryLine = new SalesOrderDeliveryLine
                    {
                        BusinessId = businessId,
                        DeliveryId = delivery.Id,
                        OrderLineId = item.Line.Id,
                        ReservationLineId = isTracked ? reservationByLine[item.Line.Id].Single().Id : null,
                        VariantId = item.Line.VariantId,
                        Quantity = item.Request.Quantity,
                        TrackStockSnapshot = isTracked,
                    };
                    deliveryLines.Add(deliveryLine);
                    item.Line.QuantityFulfilled += item.Request.Quantity;
                }
                foreach (var reservation in await db.InventoryReservations.Where(row => row.BusinessId == businessId && row.OrderId == id && row.Status == InventoryReservationStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    var ownLines = reservationLines.Where(line => line.ReservationId == reservation.Id).ToArray();
                    if (ownLines.Length > 0 && ownLines.All(line => line.QuantityConsumed + line.QuantityReleased >= line.QuantityReserved))
                    {
                        reservation.Status = ownLines.Any(line => line.QuantityReleased > 0m)
                            ? InventoryReservationStatus.Released
                            : InventoryReservationStatus.Fulfilled;
                        reservation.ClosedAtUtc = now;
                    }
                }
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                decimal costValue = 0m;
                foreach (var item in selected.Where(item => item.Line.VariantId is { } variantId && variants.TryGetValue(variantId, out var row) && row.TrackStock).OrderBy(item => item.Line.VariantId))
                {
                    var deliveryLine = deliveryLines.Single(line => line.OrderLineId == item.Line.Id);
                    var balance = await db.InventoryBalances.SingleAsync(row => row.BusinessId == businessId && row.LocationId == order.LocationId && row.VariantId == item.Line.VariantId!.Value, cancellationToken).ConfigureAwait(false);
                    var movement = await InventoryLedger.ApplyAsync(db, businessId, userId, order.LocationId, item.Line.VariantId!.Value,
                        MovementType.Sale, -item.Request.Quantity, balance.AverageUnitCost, "sales_order_delivery_line", deliveryLine.Id,
                        $"Delivery {delivery.DeliveryNumber}", now, cancellationToken).ConfigureAwait(false);
                    deliveryLine.UnitCost = movement.UnitCost;
                    deliveryLine.CostValue = -movement.ValueDelta;
                    costValue = LedgerWriter.Round(costValue + deliveryLine.CostValue);
                }

                if (orderLines.All(line => line.QuantityFulfilled + line.QuantityCancelled >= line.QuantityOrdered)) order.Status = SalesOrderStatus.Fulfilled;
                else if (orderLines.Any(line => line.QuantityFulfilled > 0m)) order.Status = SalesOrderStatus.PartiallyFulfilled;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, ["1200", "1250"], cancellationToken).ConfigureAwait(false);
                JournalEntry? entry = null;
                if (costValue > 0m)
                {
                    if (!accounts.TryGetValue("1200", out var inventoryAccount) || !inventoryAccount.IsActive || inventoryAccount.AccountType != AccountType.Asset
                        || !accounts.TryGetValue("1250", out var deliveredAccount) || !deliveredAccount.IsActive || deliveredAccount.AccountType != AccountType.Asset)
                        return MissingDeliveryAccounts();
                    entry = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, order.Currency, request.DeliveryDate,
                        $"Delivery {delivery.DeliveryNumber}", JournalSourceType.SalesOrderDelivery, delivery.Id, null,
                        [new JournalLineRequest(deliveredAccount.Id, costValue, 0m, "Delivered goods not invoiced", order.LocationId),
                         new JournalLineRequest(inventoryAccount.Id, 0m, costValue, "Inventory issued for delivery", order.LocationId)], cancellationToken).ConfigureAwait(false);
                }
                delivery.JournalEntryId = entry?.Id;
                db.SalesOrderDeliveries.Add(delivery);
                db.SalesOrderDeliveryLines.AddRange(deliveryLines);
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "sales_order_delivery",
                    EntityId = delivery.Id,
                    Action = "delivered",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { delivery.DeliveryNumber, orderId = id, costValue, lineCount = deliveryLines.Count }),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/v1/sales-orders/{id}/deliveries/{delivery.Id}", await ToDeliveryDtoAsync(db, delivery, cancellationToken).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres) { return MapPostgresException(postgres); }
    }

    private static async Task<IResult> ListDeliveriesAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var order = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (order is null) return NotFoundOrder();
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.SalesOrderDeliveries.AsNoTracking().Where(row => row.BusinessId == businessId && row.OrderId == id);
        if (CursorPaging.TryDecode(cursor, out var cursorId)) query = query.Where(row => row.Id.CompareTo(cursorId) < 0);
        var rows = await query.OrderByDescending(row => row.Id).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        var items = new List<SalesOrderDeliveryDto>(rows.Count);
        foreach (var row in rows) items.Add(await ToDeliveryDtoAsync(db, row, cancellationToken).ConfigureAwait(false));
        return Results.Ok(new PagedResult<SalesOrderDeliveryDto>(items, hasMore ? CursorPaging.Encode(rows[^1].Id) : null, size));
    }

    private static async Task ExpireOrderReservationsAsync(BillingDbContext db, Guid businessId, Guid orderId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await db.InventoryReservations
            .Where(row => row.BusinessId == businessId && row.OrderId == orderId && row.Status == InventoryReservationStatus.Active && row.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reservation in expired)
        {
            var lines = await db.InventoryReservationLines.Where(line => line.BusinessId == businessId && line.ReservationId == reservation.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var remaining = line.QuantityReserved - line.QuantityConsumed - line.QuantityReleased;
                if (remaining > 0m) line.QuantityReleased += remaining;
            }
            reservation.Status = InventoryReservationStatus.Expired;
            reservation.ClosedAtUtc = now;
            reservation.CloseReason = "Reservation expired";
            db.AuditEvents.Add(new AuditEvent { BusinessId = businessId, UserId = userId, EntityType = "inventory_reservation", EntityId = reservation.Id, Action = "expired", OccurredAtUtc = now, Summary = JsonSerializer.Serialize(new { orderId, lineCount = lines.Count }) });
        }
    }

    private static async Task<Dictionary<Guid, decimal>> GetReservedByOrderLineAsync(BillingDbContext db, Guid businessId, Guid locationId, Guid[] lineIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (lineIds.Length == 0) return [];
        var rows = await (from line in db.InventoryReservationLines.AsNoTracking()
                          join reservation in db.InventoryReservations.AsNoTracking()
                              on new { line.ReservationId, line.BusinessId } equals new { ReservationId = reservation.Id, reservation.BusinessId }
                          where line.BusinessId == businessId && lineIds.Contains(line.OrderLineId)
                                && reservation.LocationId == locationId && reservation.Status == InventoryReservationStatus.Active && reservation.ExpiresAtUtc > now
                          group line by line.OrderLineId into groupRows
                          select new { OrderLineId = groupRows.Key, Quantity = groupRows.Sum(line => line.QuantityReserved - line.QuantityConsumed - line.QuantityReleased) })
            .ToDictionaryAsync(row => row.OrderLineId, row => row.Quantity, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static async Task<Dictionary<Guid, StockVariant>> GetStockVariantsAsync(BillingDbContext db, Guid businessId, IReadOnlyList<SalesOrderLine> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Where(line => line.VariantId.HasValue).Select(line => line.VariantId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await (from variant in db.ProductVariants.AsNoTracking()
                      join product in db.Products.AsNoTracking() on new { variant.ProductId, variant.BusinessId } equals new { ProductId = product.Id, product.BusinessId }
                      where variant.BusinessId == businessId && ids.Contains(variant.Id)
                      select new StockVariant(variant.Id, variant.IsActive, product.IsActive, product.TrackStock))
            .ToDictionaryAsync(row => row.VariantId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<InventoryReservationDto> ToReservationDtoAsync(BillingDbContext db, InventoryReservation reservation, CancellationToken cancellationToken)
    {
        var lines = await db.InventoryReservationLines.AsNoTracking().Where(row => row.BusinessId == reservation.BusinessId && row.ReservationId == reservation.Id)
            .OrderBy(row => row.OrderLineId).Select(row => new InventoryReservationLineDto(row.OrderLineId, row.VariantId, row.QuantityReserved, row.QuantityConsumed, row.QuantityReleased,
                row.QuantityReserved - row.QuantityConsumed - row.QuantityReleased)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new InventoryReservationDto(reservation.Id, reservation.OrderId, reservation.Status.ToString(), reservation.ExpiresAtUtc, reservation.Version, lines);
    }

    private static async Task<SalesOrderDeliveryDto> ToDeliveryDtoAsync(BillingDbContext db, SalesOrderDelivery delivery, CancellationToken cancellationToken)
    {
        var deliveryLines = await db.SalesOrderDeliveryLines.AsNoTracking()
            .Where(row => row.BusinessId == delivery.BusinessId && row.DeliveryId == delivery.Id)
            .OrderBy(row => row.CreatedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = deliveryLines.Select(row => row.Id).ToArray();
        var allocations = ids.Length == 0
            ? new Dictionary<Guid, (decimal Quantity, decimal CostValue)>()
            : await db.DeliveryInvoiceAllocations.AsNoTracking()
                .Where(row => row.BusinessId == delivery.BusinessId && ids.Contains(row.DeliveryLineId))
                .GroupBy(row => row.DeliveryLineId)
                .Select(group => new { DeliveryLineId = group.Key, Quantity = group.Sum(row => row.Quantity), CostValue = group.Sum(row => row.CostValue) })
                .ToDictionaryAsync(row => row.DeliveryLineId, row => (row.Quantity, row.CostValue), cancellationToken).ConfigureAwait(false);
        var lines = deliveryLines.Select(row =>
        {
            var invoiced = allocations.GetValueOrDefault(row.Id);
            return new SalesOrderDeliveryLineDto(row.Id, row.OrderLineId, row.VariantId, row.Quantity, row.UnitCost, row.CostValue, row.TrackStockSnapshot,
                invoiced.Quantity, invoiced.CostValue);
        }).ToArray();
        var journal = delivery.JournalEntryId is { } journalId
            ? await db.JournalEntries.AsNoTracking().Where(row => row.BusinessId == delivery.BusinessId && row.Id == journalId).Select(row => row.EntryNumber).SingleAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return new SalesOrderDeliveryDto(delivery.Id, delivery.OrderId, delivery.DeliveryNumber, delivery.DeliveryDate, lines.Sum(line => line.Quantity), deliveryLinesCost(lines), journal, delivery.Note, delivery.CreatedAtUtc, lines);
    }

    private static decimal deliveryLinesCost(IReadOnlyList<SalesOrderDeliveryLineDto> lines) => lines.Sum(line => line.CostValue);

    private static CommercialLineAmounts SliceOrderLine(SalesOrderLine line, decimal quantity)
    {
        var mode = CommercialAmountCalculator.ParseTaxMode(line.TaxMode, "orderLine.taxMode");
        if (quantity <= 0m) return new(0m, 0m, 0m, 0m, 0m, line.TaxRate, mode, line.PricingPolicyVersion);
        var discount = LedgerWriter.Round(line.DiscountAmount * quantity / line.QuantityOrdered);
        return CommercialAmountCalculator.ComputeLine(quantity, line.UnitPrice, mode, line.TaxRate, discount, line.PricingPolicyVersion, "orderLine");
    }

    private static bool IsAccount(IReadOnlyDictionary<string, Account> accounts, string code, AccountType type)
        => accounts.TryGetValue(code, out var account) && account.IsActive && account.AccountType == type;

    private static DomainRuleException IdempotencyReuseException() => new(StatusCodes.Status409Conflict, ApiErrorCodes.SameKeyDifferentPayload,
        "The idempotency key was already used with different invoice terms.", "Use a new key for a new invoice request.");
    private static DomainRuleException NotFoundOrderException() => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The order was not found in this business.", "Check the identifier and signed-in business.");
    private static DomainRuleException ForbiddenLocationException() => new(StatusCodes.Status403Forbidden, ApiErrorCodes.PermissionDenied, "This order belongs to a location the user cannot access.", "Request access to the order location.");
    private static DomainRuleException NotFoundDeliveryException() => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The delivery was not found for this order.", "Check the delivery identifier.");
    private static DomainRuleException InvalidLinesException(string detail) => new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "The invoice lines are invalid.", detail);
    private static DomainRuleException DeliveryInvoiceQuantityConflictException() => new(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The invoice exceeds quantities delivered but not yet invoiced.", "Reload delivery allocations and retry with the remaining quantity.");
    private static DomainRuleException InvalidInvoiceDatesException() => new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "The invoice dates are invalid.", "The issue date must be on or after delivery and no later than the business date.");
    private static DomainRuleException MissingInvoiceAccountsException() => new(StatusCodes.Status409Conflict, ApiErrorCodes.ValidationFailed, "Required invoice or delivery accounts are unavailable.", "Configure active receivable, revenue, tax, delivered-not-invoiced, and cost-of-goods-sold accounts as required.");

    private static async Task<SalesOrder?> LockOrderAsync(BillingDbContext db, Guid businessId, Guid orderId, CancellationToken cancellationToken)
        => await db.SalesOrders.FromSql($"SELECT * FROM sales_orders WHERE business_id = {businessId} AND id = {orderId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static async Task LockIdempotencyKeyAsync(BillingDbContext db, Guid businessId, string scope, string key, CancellationToken cancellationToken)
    {
        var lockName = $"{businessId:N}:{scope}:{key}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetActor(ClaimsPrincipal principal, out Guid businessId, out Guid userId)
    {
        businessId = Guid.Empty;
        userId = Guid.Empty;
        return principal.TryGetBusinessId(out businessId) && Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
    }

    private static bool ValidIdempotencyKey(string value, out string key)
    {
        key = value.Trim();
        return key.Length is > 0 and <= 128 && key.All(character => !char.IsControl(character));
    }

    private static IResult MissingIdempotencyKey() => ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-128 characters."] });
    private static IResult IdempotencyConflict() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.SameKeyDifferentPayload, "The idempotency key was already used with different delivery terms.");
    private static IResult NotFoundOrder() => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The order was not found in this business.");
    private static IResult ForbiddenLocation() => ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.PermissionDenied, "This order belongs to a location the user cannot access.");
    private static IResult OrderOpenConflict() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.SalesOrderNotOpen, "The sales order is not open for fulfillment.");
    private static IResult StaleVersion() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.OptimisticConcurrencyFailure, "The sales order changed. Reload it before retrying.");
    private static IResult InvalidLines(string detail) => ApiResults.ValidationProblem("The fulfillment lines are invalid.", new Dictionary<string, string[]> { ["lines"] = [detail] });
    private static IResult InvalidNote() => ApiResults.ValidationProblem("The delivery note is too long.", new Dictionary<string, string[]> { ["note"] = ["Keep the note at 2,000 characters or fewer."] });
    private static IResult InvalidReason() => ApiResults.ValidationProblem("A release reason is required.", new Dictionary<string, string[]> { ["reason"] = ["Enter a reason of 1-500 characters."] });
    private static IResult InvalidDates() => ApiResults.ValidationProblem("The fulfillment dates are invalid.", new Dictionary<string, string[]> { ["deliveryDate"] = ["Delivery date must be on or after the order date and no later than the business date."] });
    private static IResult DeliveryQuantityConflict() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.SalesOrderNotOpen, "The delivery exceeds the confirmed outstanding order quantity.");
    private static IResult ReservationQuantityConflict() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.InsufficientStock, "The delivery exceeds its active reservation.");
    private static IResult MissingDeliveryAccounts() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.ValidationFailed, "Configure active inventory and delivered-not-invoiced asset accounts before delivery.");
    private static bool ValidNote(string? note) => string.IsNullOrWhiteSpace(note) || note.Trim().Length <= 2_000;
    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.UniqueViolation
            ? ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The fulfillment command conflicts with an existing source.")
            : ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The fulfillment command violates a business rule.");

    private sealed record StockVariant(Guid VariantId, bool VariantIsActive, bool ProductIsActive, bool TrackStock);
}
