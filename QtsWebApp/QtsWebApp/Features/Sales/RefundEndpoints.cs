using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// Cash refunds of posted sales (QB-10/QB-11). Each refund records requested quantities and disposition,
/// posts the financial and inventory effects atomically, and leaves the sale open until fully refunded.
/// </summary>
public static class RefundEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string RefundSequenceName = "refund";
    private const string RefundSequencePrefix = "R-";
    private const int MaxReasonLength = 500;
    private const int MaxNoteLength = 2_000;

    public static IEndpointRouteBuilder MapRefundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pos/sales").WithTags("POS");

        group.MapPost("/{id:guid}/refund", RefundAsync).RequireAuthorization(Permissions.SalesRefund);
        group.MapGet("/{id:guid}/refund", GetAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}/refunds", GetAllAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}/refund-position", PositionAsync).RequireAuthorization(Permissions.SalesPost);

        return app;
    }

    private static async Task<IResult> RefundAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        Guid id,
        RefundSaleRequest request,
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

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();

        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var payloadHash = RequestFingerprint.Compute(request);

        var existingRefund = await db.Refunds.AsNoTracking()
            .FirstOrDefaultAsync(refund => refund.BusinessId == businessId && refund.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existingRefund is not null)
        {
            if (!string.Equals(existingRefund.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different payload.",
                    $"Key '{idempotencyKey}' already refunded sale {existingRefund.RefundNumber}; start a new request instead of changing the payload.");
            }

            return Results.Ok(await ToDtoAsync(db, existingRefund, cancellationToken).ConfigureAwait(false));
        }

        var errors = ValidatePayload(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The refund payload is invalid.", errors);
        }

        var sale = await db.Sales
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The sale does not exist in this business.");
        }

        if (sale.Status != SaleStatus.Posted)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "Only posted sales can be refunded.");
        }

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId && payment.SaleId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (payments.Count != 1
            || payments[0].Method != PaymentMethod.Cash
            || payments[0].Status != QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RefundNotSupported,
                "This sale cannot be refunded at the register.",
                "Register refunds require exactly one confirmed cash payment; card/transfer refunds and credit notes need the invoice workflow (QB-11).");
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == request.RegisterId, cancellationToken)
            .ConfigureAwait(false);

        if (register is null || !register.IsActive || register.LocationId != sale.LocationId)
        {
            return ApiResults.ValidationProblem(
                "The refund payload is invalid.",
                new Dictionary<string, string[]> { ["registerId"] = ["A refund must name an active register of the sale's location."] });
        }

        var openSession = await db.CashRegisterSessions.AsNoTracking()
            .FirstOrDefaultAsync(session => session.BusinessId == businessId
                && session.CashRegisterId == register.Id
                && session.Status == CashRegisterSessionStatus.Open,
                cancellationToken)
            .ConfigureAwait(false);

        if (openSession is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.CashRegisterNotOpen,
                "The register has no open drawer session.",
                "Open a drawer session before refunding cash so the money leaves an accounted drawer.");
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var refund = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                // Serialize every refund for this sale before reading cumulative refunded quantities.
                var saleEntity = await db.Sales
                    .FromSql($"SELECT * FROM sales WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (saleEntity is null || saleEntity.Status != SaleStatus.Posted)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.AlreadyRefunded,
                        "This sale is not open for further refunds.",
                        "Only a posted sale with remaining line quantities can be refunded.");
                }

                var session = await db.CashRegisterSessions
                    .FromSql($"SELECT * FROM cash_register_sessions WHERE id = {openSession.Id} AND business_id = {businessId} FOR UPDATE")
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (session.Status != CashRegisterSessionStatus.Open)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.CashRegisterNotOpen,
                        "The drawer session was closed.",
                        "Open a new session before refunding cash.");
                }

                var saleLines = await db.SaleLines
                    .FromSql($"SELECT * FROM sale_lines WHERE business_id = {businessId} AND sale_id = {id} ORDER BY line_no FOR UPDATE")
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var priorLines = await (from refundLine in db.RefundLines.AsNoTracking()
                    join priorRefund in db.Refunds.AsNoTracking() on new { refundLine.RefundId, refundLine.BusinessId } equals new { RefundId = priorRefund.Id, priorRefund.BusinessId }
                    where priorRefund.BusinessId == businessId && priorRefund.SaleId == id
                    select new PriorRefundLine(refundLine.SaleLineId, refundLine.Quantity, refundLine.TaxAmount, refundLine.LineTotal))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var entryDate = LedgerWriter.LocalDate(business.TimeZone, now);

                var refundedByLine = priorLines.GroupBy(line => line.SaleLineId)
                    .ToDictionary(group => group.Key, group => new PriorLineTotals(
                        group.Sum(line => line.Quantity),
                        group.Sum(line => line.TaxAmount),
                        group.Sum(line => line.LineTotal)));
                var requestedLines = request.Lines is null or { Count: 0 }
                    ? saleLines
                        .Where(line => !refundedByLine.TryGetValue(line.Id, out var prior) || prior.Quantity < line.Quantity)
                        .Select(line => new RefundLineRequest(
                            line.Id,
                            line.Quantity - (refundedByLine.TryGetValue(line.Id, out var prior) ? prior.Quantity : 0m),
                            QtsBilling.Contracts.Sales.RefundDisposition.Restock))
                        .ToList()
                    : request.Lines;

                var refundLines = new List<CalculatedRefundLine>(requestedLines.Count);
                var saleLineById = saleLines.ToDictionary(line => line.Id);
                foreach (var requested in requestedLines.OrderBy(line => saleLineById.TryGetValue(line.SaleLineId, out var matched) ? matched.LineNo : int.MaxValue))
                {
                    if (!saleLineById.TryGetValue(requested.SaleLineId, out var saleLine))
                    {
                        throw InvalidRefundLine("Every refund line must refer to a line on this sale.");
                    }

                    var prior = refundedByLine.GetValueOrDefault(requested.SaleLineId) ?? new PriorLineTotals(0m, 0m, 0m);
                    var remaining = LedgerWriter.Round(saleLine.Quantity - prior.Quantity);
                    if (requested.Quantity <= 0m || requested.Quantity > remaining)
                    {
                        throw InvalidRefundLine($"The remaining refundable quantity for line {saleLine.LineNo} is {remaining}.");
                    }

                    if (!Enum.IsDefined(requested.Disposition))
                    {
                        throw InvalidRefundLine($"The disposition for line {saleLine.LineNo} is not supported.");
                    }

                    var amounts = CalculateLineRefund(saleLine, requested.Quantity, remaining, prior);
                    refundLines.Add(new CalculatedRefundLine(saleLine, requested.Quantity, requested.Disposition, amounts.TaxAmount, amounts.LineTotal));
                }

                if (refundLines.Count == 0)
                {
                    throw InvalidRefundLine("The sale has no remaining quantity to refund.");
                }

                var refundAmount = LedgerWriter.Round(refundLines.Sum(line => line.LineTotal));
                var refundedAmount = LedgerWriter.Round(priorLines.Sum(line => line.LineTotal));
                if (refundAmount <= 0m || refundAmount > LedgerWriter.Round(saleEntity.TotalAmount - refundedAmount))
                {
                    throw InvalidRefundLine("The requested refund exceeds the remaining sale amount.");
                }

                // The money has to come out of a drawer that holds it. The session is already locked for update,
                // so this is the same derived figure the register workbench shows, judged before anything is
                // written (specification D07, task 21 recipe step 4: insufficient drawer cash is an outcome).
                await CashDrawerGuards.EnsureCashCoversAsync(
                    db,
                    businessId,
                    session,
                    refundAmount,
                    $"refund of {refundAmount.ToString(CultureInfo.InvariantCulture)} for sale {saleEntity.SaleNumber}",
                    cancellationToken).ConfigureAwait(false);

                var accounts = await LedgerWriter.ResolveAccountsAsync(
                    db,
                    businessId,
                    ["1000", "1200", "2100", "4010", "5000", "5200"],
                    cancellationToken).ConfigureAwait(false);
                var missingAccounts = new[] { "1000", "1200", "2100", "4010", "5000", "5200" }
                    .Where(code => !accounts.TryGetValue(code, out var account) || !account.IsActive)
                    .ToArray();
                if (missingAccounts.Length > 0)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        $"The chart of accounts is missing: {string.Join(", ", missingAccounts)}.",
                        "Configure the cash, inventory, tax, sales returns, COGS, and shrinkage accounts before refunding.");
                }

                var refundNumber = await LedgerWriter.AllocateNumberAsync(
                    db,
                    businessId,
                    RefundSequenceName,
                    RefundSequencePrefix,
                    entryDate.Year,
                    cancellationToken).ConfigureAwait(false);

                var refund = new Refund
                {
                    BusinessId = businessId,
                    SaleId = saleEntity.Id,
                    LocationId = saleEntity.LocationId,
                    RefundNumber = refundNumber,
                    Currency = saleEntity.Currency,
                    Method = PaymentMethod.Cash,
                    Amount = refundAmount,
                    Reason = request.Reason.Trim(),
                    Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                    CashRegisterSessionId = session.Id,
                    PostedAtUtc = now,
                    PostedByUserId = userId,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                db.Refunds.Add(refund);
                var refundLineEntities = new Dictionary<Guid, RefundLine>();
                foreach (var calculated in refundLines)
                {
                    var refundLine = new RefundLine
                    {
                        BusinessId = businessId,
                        RefundId = refund.Id,
                        SaleLineId = calculated.SaleLine.Id,
                        LineNo = calculated.SaleLine.LineNo,
                        ProductNameSnapshot = calculated.SaleLine.ProductNameSnapshot,
                        SkuSnapshot = calculated.SaleLine.SkuSnapshot,
                        Quantity = calculated.Quantity,
                        UnitPrice = calculated.SaleLine.UnitPrice,
                        TaxAmount = calculated.TaxAmount,
                        LineTotal = calculated.LineTotal,
                        UnitCostSnapshot = calculated.SaleLine.UnitCostSnapshot,
                        Disposition = (QtsBilling.Persistence.Postgres.RefundDisposition)calculated.Disposition,
                    };
                    db.RefundLines.Add(refundLine);
                    refundLineEntities.Add(calculated.SaleLine.Id, refundLine);
                }

                // Only lines that took stock on the sale give stock back; the movement rows are the evidence.
                var saleLineIds = saleLines.Select(line => line.Id).ToArray();

                var stockedLineIds = await db.InventoryMovements.AsNoTracking()
                    .Where(movement => movement.BusinessId == businessId
                        && movement.MovementType == MovementType.Sale
                        && movement.ReferenceType == "sale_line"
                        && saleLineIds.Contains(movement.ReferenceId))
                    .Select(movement => movement.ReferenceId)
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var stocked = stockedLineIds.ToHashSet();

                decimal restockedCost = 0m;
                decimal damagedCost = 0m;
                foreach (var calculated in refundLines.Where(line => stocked.Contains(line.SaleLine.Id)).OrderBy(line => line.SaleLine.VariantId))
                {
                    if (calculated.Disposition == QtsBilling.Contracts.Sales.RefundDisposition.Restock)
                    {
                        var movement = await InventoryLedger.ApplyAsync(
                            db,
                            businessId,
                            userId,
                            saleEntity.LocationId,
                            calculated.SaleLine.VariantId,
                            MovementType.SaleReversal,
                            calculated.Quantity,
                            calculated.SaleLine.UnitCostSnapshot,
                            "refund_line",
                            refundLineEntities[calculated.SaleLine.Id].Id,
                            $"Refund {refundNumber}",
                            now,
                            cancellationToken).ConfigureAwait(false);
                        restockedCost = LedgerWriter.Round(restockedCost + movement.ValueDelta);
                    }
                    else
                    {
                        damagedCost = LedgerWriter.Round(damagedCost + calculated.Quantity * calculated.SaleLine.UnitCostSnapshot);
                    }
                }

                var returnedTax = LedgerWriter.Round(refundLines.Sum(line => line.TaxAmount));
                var returnedRevenue = LedgerWriter.Round(refundAmount - returnedTax);
                var journalLines = new List<JournalLineRequest>
                {
                    new(accounts["4010"].Id, returnedRevenue, 0m, "Sales returns", saleEntity.LocationId),
                    new(accounts["2100"].Id, returnedTax, 0m, "Tax returned", saleEntity.LocationId),
                    new(accounts["1000"].Id, 0m, refundAmount, $"Cash refund {refundNumber}", saleEntity.LocationId),
                };
                if (restockedCost > 0m)
                {
                    journalLines.Add(new(accounts["1200"].Id, restockedCost, 0m, "Restocked inventory", saleEntity.LocationId));
                    journalLines.Add(new(accounts["5000"].Id, 0m, restockedCost, "COGS reversal", saleEntity.LocationId));
                }
                if (damagedCost > 0m)
                {
                    journalLines.Add(new(accounts["5200"].Id, damagedCost, 0m, "Damaged return write-off", saleEntity.LocationId));
                    journalLines.Add(new(accounts["5000"].Id, 0m, damagedCost, "COGS reversal for damaged return", saleEntity.LocationId));
                }
                var refundEntry = await LedgerWriter.PostWithinTransactionAsync(
                    db,
                    businessId,
                    userId,
                    saleEntity.Currency,
                    entryDate,
                    $"Refund of {saleEntity.SaleNumber}",
                    JournalSourceType.Refund,
                    refund.Id,
                    reversalOfEntryId: null,
                    journalLines,
                    cancellationToken).ConfigureAwait(false);
                refund.JournalEntryId = refundEntry.Id;

                db.CashRegisterMovements.Add(new CashRegisterMovement
                {
                    BusinessId = businessId,
                    CashRegisterId = register.Id,
                    CashRegisterSessionId = session.Id,
                    MovementType = CashMovementType.Refund,
                    Amount = refundAmount,
                    Currency = saleEntity.Currency,
                    Reason = $"Refund {refundNumber}: {request.Reason.Trim()}",
                    SaleId = saleEntity.Id,
                    CreatedByUserId = userId,
                    OccurredAtUtc = now,
                });

                var paymentEntity = await db.Payments
                    .FirstAsync(payment => payment.BusinessId == businessId && payment.SaleId == saleEntity.Id, cancellationToken)
                    .ConfigureAwait(false);

                var allQuantitiesRefunded = saleLines.All(line =>
                {
                    var priorQty = refundedByLine.TryGetValue(line.Id, out var previous) ? previous.Quantity : 0m;
                    var thisQty = refundLines.Where(candidate => candidate.SaleLine.Id == line.Id).Sum(candidate => candidate.Quantity);
                    return LedgerWriter.Round(priorQty + thisQty) >= line.Quantity;
                });
                var allMoneyRefunded = LedgerWriter.Round(refundedAmount + refundAmount) >= saleEntity.TotalAmount;
                if (allQuantitiesRefunded)
                {
                    saleEntity.Status = SaleStatus.Reversed;
                }
                if (allMoneyRefunded)
                {
                    paymentEntity.Status = QtsBilling.Persistence.Postgres.PaymentStatus.Refunded;
                    saleEntity.PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus.Refunded;
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "refund",
                    EntityId = refund.Id,
                    Action = "posted",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        refundNumber,
                        saleNumber = saleEntity.SaleNumber,
                        saleId = saleEntity.Id,
                        amount = refundAmount,
                        reversalEntry = refundEntry.EntryNumber,
                        registerId = register.Id,
                        sessionId = session.Id,
                        reason = request.Reason.Trim(),
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return refund;
            }).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/pos/sales/{sale.Id}/refund",
                await ToDtoAsync(db, refund, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var saleExists = await db.Sales.AsNoTracking()
            .AnyAsync(sale => sale.BusinessId == businessId && sale.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (!saleExists)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The sale does not exist in this business.");
        }

        var refund = await db.Refunds.AsNoTracking()
            .OrderByDescending(candidate => candidate.PostedAtUtc)
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.SaleId == id, cancellationToken)
            .ConfigureAwait(false);

        if (refund is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "This sale has no refund.");
        }

        return Results.Ok(await ToDtoAsync(db, refund, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetAllAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var saleExists = await db.Sales.AsNoTracking()
            .AnyAsync(sale => sale.BusinessId == businessId && sale.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (!saleExists)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The sale does not exist in this business.");
        }

        var refunds = await db.Refunds.AsNoTracking()
            .Where(refund => refund.BusinessId == businessId && refund.SaleId == id)
            .OrderBy(refund => refund.PostedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<RefundDto>(refunds.Count);
        foreach (var refund in refunds)
        {
            results.Add(await ToDtoAsync(db, refund, cancellationToken).ConfigureAwait(false));
        }

        return Results.Ok(results);
    }

    /// <summary>
    /// What the server still allows to be refunded on one sale (specification task 21, recipe step 2): the sold and
    /// already-refunded quantities, the money left on each line at the original snapshots and cost, whether a
    /// register refund is possible at all and why not, and the drawers of that branch that could pay it with the
    /// cash the server can prove they hold. A screen shows these figures as limits; its own totals are previews.
    /// </summary>
    private static async Task<IResult> PositionAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var sale = await db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The sale does not exist in this business.");
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        var saleLines = await db.SaleLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.SaleId == id)
            .OrderBy(line => line.LineNo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var priorLines = await (from refundLine in db.RefundLines.AsNoTracking()
            join priorRefund in db.Refunds.AsNoTracking()
                on new { refundLine.RefundId, refundLine.BusinessId } equals new { RefundId = priorRefund.Id, priorRefund.BusinessId }
            where priorRefund.BusinessId == businessId && priorRefund.SaleId == id
            select new PriorRefundLine(refundLine.SaleLineId, refundLine.Quantity, refundLine.TaxAmount, refundLine.LineTotal))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var refundedByLine = priorLines.GroupBy(line => line.SaleLineId)
            .ToDictionary(group => group.Key, group => new PriorLineTotals(
                group.Sum(line => line.Quantity),
                group.Sum(line => line.TaxAmount),
                group.Sum(line => line.LineTotal)));

        // Only the lines that actually took stock can give it back, and the workbench has to say which is which
        // before an operator chooses a disposition (task 21, recipe step 2).
        var lineIds = saleLines.Select(line => line.Id).ToArray();
        var stockedLineIds = await db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId
                && movement.MovementType == MovementType.Sale
                && movement.ReferenceType == "sale_line"
                && lineIds.Contains(movement.ReferenceId))
            .Select(movement => movement.ReferenceId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var stocked = stockedLineIds.ToHashSet();

        var linePositions = new List<SaleRefundLinePositionDto>(saleLines.Count);

        foreach (var line in saleLines)
        {
            var prior = refundedByLine.GetValueOrDefault(line.Id) ?? new PriorLineTotals(0m, 0m, 0m);
            var remaining = LedgerWriter.Round(line.Quantity - prior.Quantity);
            var remainingAmount = remaining <= 0m ? 0m : CalculateLineRefund(line, remaining, remaining, prior).LineTotal;

            linePositions.Add(new SaleRefundLinePositionDto(
                line.Id,
                line.LineNo,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.Quantity,
                prior.Quantity,
                remaining,
                line.UnitPrice,
                line.DiscountAmount,
                line.TaxRate,
                line.TaxAmount,
                line.LineTotal,
                remainingAmount,
                line.UnitCostSnapshot,
                stocked.Contains(line.Id),
                SalePosting.EffectiveTaxMode(line)));
        }

        var refundedAmount = LedgerWriter.Round(priorLines.Sum(line => line.LineTotal));
        var remainingRefundable = LedgerWriter.Round(sale.TotalAmount - refundedAmount);

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId && payment.SaleId == id)
            .OrderBy(payment => payment.ReceivedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var payment = payments.Count == 1 ? payments[0] : null;

        // Every register of the branch is listed, with the cash its open drawer can be proved to hold: where the
        // money is, and whether it is enough, is the operator's question (requirement R07).
        var registers = await db.CashRegisters.AsNoTracking()
            .Where(register => register.BusinessId == businessId && register.LocationId == sale.LocationId)
            .OrderBy(register => register.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var registerIds = registers.Select(register => register.Id).ToArray();

        var openSessions = await db.CashRegisterSessions.AsNoTracking()
            .Where(session => session.BusinessId == businessId
                && session.Status == CashRegisterSessionStatus.Open
                && registerIds.Contains(session.CashRegisterId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var drawers = new List<RefundDrawerOptionDto>(registers.Count);

        foreach (var register in registers)
        {
            var session = openSessions.FirstOrDefault(candidate => candidate.CashRegisterId == register.Id);

            if (session is null)
            {
                drawers.Add(new RefundDrawerOptionDto(
                    register.Id,
                    register.Name,
                    null,
                    null,
                    null,
                    business?.BaseCurrency ?? sale.Currency,
                    register.IsActive ? "No open drawer" : "Retired register"));
                continue;
            }

            var expected = await CashDrawerGuards.ExpectedCashAsync(db, businessId, session, cancellationToken)
                .ConfigureAwait(false);

            drawers.Add(new RefundDrawerOptionDto(
                register.Id,
                register.Name,
                session.Id,
                expected,
                LedgerWriter.Round(Math.Min(expected, remainingRefundable)),
                session.Currency,
                !register.IsActive
                    ? "Retired register with an open drawer"
                    : expected > 0m
                        ? "Open drawer holding " + Format(expected)
                        : "Open drawer holding no cash"));
        }

        // The same verdicts the refund path itself will reach, stated before anybody promises money (task 21,
        // recipe steps 2 and 4).
        string? refusalCode = null;
        string? refusalReason = null;

        if (sale.Status != SaleStatus.Posted)
        {
            refusalCode = ApiErrorCodes.AlreadyRefunded;
            refusalReason = "This sale is " + sale.Status + ", so nothing more can be refunded against it.";
        }
        else if (payment is null || payment.Method != PaymentMethod.Cash)
        {
            refusalCode = ApiErrorCodes.RefundNotSupported;
            refusalReason = payments.Count != 1
                ? "A register refund needs exactly one payment row on the sale; this sale has " + payments.Count
                    + ", so the money is reconciled through the invoice workflow instead."
                : "This sale was taken as " + payments[0].Method + ". A register refund pays cash back out of a "
                    + "drawer; card or transfer money is returned through the invoice workflow (QB-11).";
        }
        else if (payment.Status != QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
        {
            refusalCode = ApiErrorCodes.PaymentUnconfirmed;
            refusalReason = "The payment on this sale is " + payment.Status + ", not confirmed cash. Nothing has been "
                + "collected that could be paid back, so the attempt has to be resolved first.";
        }
        else if (remainingRefundable <= 0m || linePositions.All(line => line.RemainingQuantity <= 0m))
        {
            refusalCode = ApiErrorCodes.AlreadyRefunded;
            refusalReason = "The whole sale has been refunded: " + Format(refundedAmount) + " of "
                + Format(sale.TotalAmount) + " is already back.";
        }
        else if (drawers.All(drawer => drawer.OpenSessionId is null))
        {
            refusalCode = ApiErrorCodes.CashRegisterNotOpen;
            refusalReason = "No register of this branch has an open drawer, so there is no accounted till to pay "
                + "the cash out of. Open a drawer on the register workbench first.";
        }

        // Whether one particular drawer holds enough cash is answered per drawer above (AvailableForRefund) and
        // authoritatively at commit with INSUFFICIENT_DRAWER_CASH: the branch may have several open tills, so the
        // position never declares the whole sale unrefundable because one of them is short.

        return Results.Ok(new SaleRefundPositionDto(
            sale.Id,
            sale.SaleNumber,
            sale.Status.ToString(),
            sale.Currency,
            sale.TotalAmount,
            sale.TaxAmount,
            refundedAmount,
            remainingRefundable,
            payment?.Method.ToString() ?? (payments.Count == 0 ? "None" : "Several"),
            payment?.Status.ToString() ?? (payments.Count == 0 ? "None" : "Several"),
            RefundableAtRegister: refusalCode is null,
            refusalCode,
            refusalReason,
            linePositions,
            drawers));
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static Dictionary<string, string[]> ValidatePayload(RefundSaleRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = request.Reason?.Trim() ?? string.Empty;

        if (reason.Length is 0 or > MaxReasonLength)
        {
            errors["reason"] = [$"A refund reason of 1-{MaxReasonLength} characters is required (audited with the actor and time)."];
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            errors["note"] = [$"A note of at most {MaxNoteLength} characters is supported."];
        }

        if (request.RegisterId == Guid.Empty)
        {
            errors["registerId"] = ["The register that pays the refund is required."];
        }

        if (request.Lines is { Count: > 0 })
        {
            if (request.Lines.Any(line => line.SaleLineId == Guid.Empty || line.Quantity <= 0m || decimal.Round(line.Quantity, 6) != line.Quantity))
            {
                errors["lines"] = ["Each refund line needs a sale line id and a positive quantity with at most six decimal places."];
            }
            else if (request.Lines.Select(line => line.SaleLineId).Distinct().Count() != request.Lines.Count)
            {
                errors["lines"] = ["A sale line may appear at most once in a refund request."];
            }
            else if (request.Lines.Any(line => !Enum.IsDefined(line.Disposition)))
            {
                errors["lines"] = ["Each refund line needs a supported disposition."];
            }
        }

        return errors;
    }

    private static async Task<RefundDto> ToDtoAsync(BillingDbContext db, Refund refund, CancellationToken cancellationToken)
    {
        var sale = await db.Sales.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == refund.SaleId, cancellationToken)
            .ConfigureAwait(false);

        var lines = await db.RefundLines.AsNoTracking()
            .Where(line => line.RefundId == refund.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new RefundLineDto(
                line.SaleLineId,
                line.LineNo,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.Quantity,
                line.UnitPrice,
                line.TaxAmount,
                line.LineTotal,
                line.UnitCostSnapshot,
                line.Disposition.ToString()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? journalEntryNumber = null;

        if (refund.JournalEntryId is { } entryId)
        {
            journalEntryNumber = await db.JournalEntries.AsNoTracking()
                .Where(entry => entry.Id == entryId)
                .Select(entry => entry.EntryNumber)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return new RefundDto(
            refund.Id,
            refund.RefundNumber,
            refund.SaleId,
            sale.SaleNumber,
            refund.Amount,
            refund.Currency,
            refund.Method.ToString(),
            refund.Reason,
            refund.Note,
            refund.PostedAtUtc,
            refund.PostedByUserId,
            refund.CashRegisterSessionId,
            journalEntryNumber,
            lines);
    }

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_refunds_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same key committed first.",
                    "Retry the same payload: the stored refund will be returned instead of refunding twice."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The refund write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The refund violates an integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The refund references a record outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The refund failed."),
        };

    private static DomainRuleException InvalidRefundLine(string detail)
        => new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.ValidationFailed,
            "The refund lines are invalid.",
            detail);

    private static (decimal TaxAmount, decimal LineTotal) CalculateLineRefund(
        SaleLine saleLine,
        decimal requestedQuantity,
        decimal remainingQuantity,
        PriorLineTotals prior)
    {
        if (requestedQuantity == remainingQuantity)
        {
            return (
                LedgerWriter.Round(saleLine.TaxAmount - prior.TaxAmount),
                LedgerWriter.Round(saleLine.LineTotal - prior.LineTotal));
        }

        var discount = saleLine.Quantity == 0m
            ? 0m
            : LedgerWriter.Round(saleLine.DiscountAmount * requestedQuantity / saleLine.Quantity);
        var storedTaxMode = SalePosting.EffectiveTaxMode(saleLine);
        var taxMode = string.Equals(storedTaxMode, "Unspecified", StringComparison.OrdinalIgnoreCase)
            ? QtsModels.ValueObjects.CommercialTaxMode.Exclusive
            : CommercialAmountCalculator.ParseTaxMode(storedTaxMode, "taxMode");
        var amounts = CommercialAmountCalculator.ComputeLine(
            requestedQuantity,
            saleLine.UnitPrice,
            taxMode,
            saleLine.TaxRate,
            discount,
            field: "lines");
        return (amounts.TaxAmount, amounts.PayableAmount);
    }

    private sealed record PriorRefundLine(Guid SaleLineId, decimal Quantity, decimal TaxAmount, decimal LineTotal);
    private sealed record PriorLineTotals(decimal Quantity, decimal TaxAmount, decimal LineTotal);
    private sealed record CalculatedRefundLine(
        SaleLine SaleLine,
        decimal Quantity,
        QtsBilling.Contracts.Sales.RefundDisposition Disposition,
        decimal TaxAmount,
        decimal LineTotal);
}
