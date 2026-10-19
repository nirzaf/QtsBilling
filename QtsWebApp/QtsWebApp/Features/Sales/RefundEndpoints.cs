using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// Full refunds of posted cash sales (QB-10). One transaction returns the stock, posts the reversal entry,
/// records the cash refund against the drawer session, flips the payment state and writes the audit event.
/// Partial refunds and credit notes are QB-11 (spec §3.6 refunds tied to the original confirmed payment).
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

        var alreadyRefunded = sale.Status == SaleStatus.Reversed
            || await db.Refunds.AsNoTracking().AnyAsync(refund => refund.BusinessId == businessId && refund.SaleId == id, cancellationToken).ConfigureAwait(false);

        if (alreadyRefunded)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.AlreadyRefunded,
                "This sale was already refunded.",
                "A posted sale can be refunded at most once; partial refunds and credit notes arrive with QB-11.");
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

                var saleEntity = await db.Sales
                    .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (saleEntity.Status != SaleStatus.Posted)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.AlreadyRefunded,
                        "This sale was already refunded.",
                        "A posted sale can be refunded at most once.");
                }

                var saleLines = await db.SaleLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.SaleId == id)
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var originalEntry = await db.JournalEntries.AsNoTracking()
                    .FirstOrDefaultAsync(entry => entry.BusinessId == businessId
                        && entry.SourceType == JournalSourceType.Sale
                        && entry.SourceId == id,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (originalEntry is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "The sale has no posted journal entry to reverse.",
                        "Every posted sale must have a source journal entry; this is a data-integrity error to investigate.");
                }

                var alreadyReversed = await db.JournalEntries.AsNoTracking()
                    .AnyAsync(entry => entry.BusinessId == businessId
                        && entry.SourceType == JournalSourceType.Reversal
                        && entry.SourceId == originalEntry.Id,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (alreadyReversed)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.AlreadyRefunded,
                        "The sale journal entry already has a reversal.",
                        "A posted entry can be reversed at most once.");
                }

                var originalLines = await db.JournalLines.AsNoTracking()
                    .Where(line => line.JournalEntryId == originalEntry.Id)
                    .OrderBy(line => line.LineNo)
                    .Select(line => new JournalLineRequest(line.AccountId, line.CreditAmount, line.DebitAmount, line.Description, line.LocationId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var entryDate = LedgerWriter.LocalDate(business.TimeZone, now);

                var reversal = await LedgerWriter.PostWithinTransactionAsync(
                    db,
                    businessId,
                    userId,
                    saleEntity.Currency,
                    entryDate,
                    $"Refund of {saleEntity.SaleNumber}",
                    JournalSourceType.Reversal,
                    originalEntry.Id,
                    reversalOfEntryId: originalEntry.Id,
                    originalLines,
                    cancellationToken).ConfigureAwait(false);

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
                    Amount = saleEntity.TotalAmount,
                    Reason = request.Reason.Trim(),
                    Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                    CashRegisterSessionId = session.Id,
                    JournalEntryId = reversal.Id,
                    PostedAtUtc = now,
                    PostedByUserId = userId,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                db.Refunds.Add(refund);

                foreach (var line in saleLines)
                {
                    db.RefundLines.Add(new RefundLine
                    {
                        BusinessId = businessId,
                        RefundId = refund.Id,
                        SaleLineId = line.Id,
                        LineNo = line.LineNo,
                        ProductNameSnapshot = line.ProductNameSnapshot,
                        SkuSnapshot = line.SkuSnapshot,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        TaxAmount = line.TaxAmount,
                        LineTotal = line.LineTotal,
                        UnitCostSnapshot = line.UnitCostSnapshot,
                    });
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

                foreach (var line in saleLines.Where(line => stocked.Contains(line.Id)).OrderBy(line => line.VariantId))
                {
                    await InventoryLedger.ApplyAsync(
                        db,
                        businessId,
                        userId,
                        saleEntity.LocationId,
                        line.VariantId,
                        MovementType.SaleReversal,
                        line.Quantity,
                        line.UnitCostSnapshot,
                        "sale_line",
                        line.Id,
                        $"Refund {refundNumber}",
                        now,
                        allowNegative: false,
                        cancellationToken).ConfigureAwait(false);
                }

                db.CashRegisterMovements.Add(new CashRegisterMovement
                {
                    BusinessId = businessId,
                    CashRegisterId = register.Id,
                    CashRegisterSessionId = session.Id,
                    MovementType = CashMovementType.Refund,
                    Amount = saleEntity.TotalAmount,
                    Currency = saleEntity.Currency,
                    Reason = $"Refund {refundNumber}: {request.Reason.Trim()}",
                    SaleId = saleEntity.Id,
                    CreatedByUserId = userId,
                    OccurredAtUtc = now,
                });

                var paymentEntity = await db.Payments
                    .FirstAsync(payment => payment.BusinessId == businessId && payment.SaleId == saleEntity.Id, cancellationToken)
                    .ConfigureAwait(false);

                paymentEntity.Status = QtsBilling.Persistence.Postgres.PaymentStatus.Refunded;
                saleEntity.Status = SaleStatus.Reversed;
                saleEntity.PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus.Refunded;

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
                        amount = saleEntity.TotalAmount,
                        reversalEntry = reversal.EntryNumber,
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
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.SaleId == id, cancellationToken)
            .ConfigureAwait(false);

        if (refund is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "This sale has no refund.");
        }

        return Results.Ok(await ToDtoAsync(db, refund, cancellationToken).ConfigureAwait(false));
    }

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
                line.UnitCostSnapshot))
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
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_refunds_business_sale" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.AlreadyRefunded,
                    "This sale was already refunded."),
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
}
