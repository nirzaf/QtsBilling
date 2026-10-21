using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Pos;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Features.Sales;
using QtsWebApp.Infrastructure;
using TerminalCaptureEnvelope = QtsBilling.Contracts.Sales.TerminalCaptureEnvelope;
using TerminalCaptureVerification = QtsBilling.Contracts.Sales.TerminalCaptureVerification;

namespace QtsWebApp.Features.Invoices;

/// <summary>
/// Immutable invoice and credit workflow (QB-11). Drafts are editable; issued invoices retain their original
/// terms and partial credit notes record separate line snapshots and journals.
/// </summary>
public static class InvoiceEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string InvoiceSequenceName = "invoice";
    private const string InvoiceSequencePrefix = "INV-";
    private const string CreditSequenceName = "credit";
    private const string CreditSequencePrefix = "CN-";
    private const int MaxLines = 100;
    private const int MaxNameLength = 200;
    private const int MaxReasonLength = 500;
    private const int MaxNoteLength = 2_000;
    private const int MoneyScale = 4;

    private const string CashAccountCode = "1000";
    private const string BankAccountCode = "1010";
    private const string ReceivableAccountCode = "1100";
    private const string TaxAccountCode = "2100";
    private const string RevenueAccountCode = "4000";
    private const string InventoryAccountCode = "1200";
    private const string CogsAccountCode = "5000";

    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invoices").WithTags("Invoices");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapPost("/from-sale", CreateFromSaleAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapPost("/{id:guid}/issue", IssueAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPost("/{id:guid}/void", VoidAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapPost("/{id:guid}/payments", AddPaymentAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPost("/{id:guid}/credit-notes", CreditAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapGet("/{id:guid}/print", PrintAsync).RequireAuthorization(Permissions.InvoicesIssue);

        app.MapPost("/api/v1/payments/{id:guid}/clear", ClearPaymentAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.AccountingPost);
        app.MapPost("/api/v1/payments/{id:guid}/bounce", BouncePaymentAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.AccountingPost);

        return app;
    }

    private static async Task<IResult> ClearPaymentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var key = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (key.Length is 0 or > 128)
        {
            return ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]>
            {
                [IdempotencyHeader] = ["Send a client-generated key of 1-128 characters."],
            });
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "payment.clear",
                key,
                new { PaymentId = id, Action = "clear" },
                async () =>
                {
                    var payment = await db.Payments
                        .FromSql($"SELECT * FROM payments WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (payment is null)
                    {
                        throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The payment does not exist in this business.", "Check the identifier and signed-in business.");
                    }

                    if (payment.Status != PaymentStatus.Pending
                        || payment.Method is not (PaymentMethod.Cheque or PaymentMethod.BankTransfer))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Only a pending cheque or bank transfer can be cleared.", "Clear each pending tender once after it is confirmed by the bank.");
                    }

                    if (payment.InvoiceId is null && payment.SaleId is null && payment.CustomerId is null)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This payment is not linked to an open receivable.", "Only invoice and POS receivable payments can be cleared through this route.");
                    }

                    var customerDeposit = payment.CustomerId is not null && payment.InvoiceId is null && payment.SaleId is null;
                    var accountCodes = customerDeposit
                        ? new[] { BankAccountCode, ReceivableAccountCode, "2105" }
                        : new[] { BankAccountCode, ReceivableAccountCode };
                    var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, accountCodes, cancellationToken)
                        .ConfigureAwait(false);
                    RejectMissingAccounts(accounts, BankAccountCode, ReceivableAccountCode);
                    var bankAccount = accounts[BankAccountCode];
                    if (payment.BankAccountId is { } bankAccountId)
                    {
                        bankAccount = await db.Accounts.AsNoTracking()
                            .FirstOrDefaultAsync(account => account.BusinessId == businessId && account.Id == bankAccountId && account.IsActive && account.AccountType == AccountType.Asset, cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The selected bank account is unavailable.", "Choose an active asset account in this business.");
                    }

                    Invoice? invoice = null;
                    var invoiceAllocations = new List<(Invoice Invoice, decimal Amount)>();
                    Guid? locationId = null;
                    if (payment.InvoiceId is { } invoiceId)
                    {
                        invoice = await db.Invoices
                            .FromSql($"SELECT * FROM invoices WHERE id = {invoiceId} AND business_id = {businessId} FOR UPDATE")
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The receivable invoice no longer exists.", "The payment cannot be cleared against a missing invoice.");
                        if (invoice.BalanceDue < payment.Amount)
                        {
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The invoice balance is below the pending payment amount.", "Reconcile the invoice balance before clearing this tender.");
                        }
                        locationId = invoice.LocationId;
                    }

                    if (customerDeposit)
                    {
                        var allocationRows = await db.PaymentAllocations.AsNoTracking()
                            .Where(allocation => allocation.BusinessId == businessId && allocation.PaymentId == payment.Id)
                            .OrderBy(allocation => allocation.InvoiceId)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var allocatedTotal = LedgerWriter.Round(allocationRows.Sum(allocation => allocation.AllocatedAmount));
                        if (LedgerWriter.Round(allocatedTotal + payment.UnallocatedAmount) != payment.Amount)
                        {
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The customer receipt allocations do not reconcile to the payment amount.", "Reconcile the customer payment before clearing it.");
                        }

                        var invoiceIds = allocationRows.Select(allocation => allocation.InvoiceId).Distinct().Order().ToArray();
                        var lockedInvoices = invoiceIds.Length == 0
                            ? new List<Invoice>()
                            : await db.Invoices
                                .FromSql($"SELECT * FROM invoices WHERE business_id = {businessId} AND id = ANY({invoiceIds}) ORDER BY id FOR UPDATE")
                                .ToListAsync(cancellationToken)
                                .ConfigureAwait(false);
                        var invoiceById = lockedInvoices.ToDictionary(candidate => candidate.Id);
                        if (invoiceById.Count != invoiceIds.Length)
                        {
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "A customer receipt invoice no longer exists.", "The receipt cannot be cleared until its allocations are reconciled.");
                        }

                        foreach (var allocation in allocationRows)
                        {
                            var allocatedInvoice = invoiceById[allocation.InvoiceId];
                            if (allocatedInvoice.CustomerId != payment.CustomerId
                                || allocatedInvoice.Currency != payment.Currency
                                || allocatedInvoice.BalanceDue < allocation.AllocatedAmount
                                || allocatedInvoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                            {
                                throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "A customer receipt allocation is no longer valid.", "Reconcile the invoice before clearing this receipt.");
                            }
                            invoiceAllocations.Add((allocatedInvoice, allocation.AllocatedAmount));
                        }
                        accounts.TryGetValue("2105", out var depositAccount);
                        if (payment.UnallocatedAmount > 0
                            && (depositAccount is null || !depositAccount.IsActive || depositAccount.AccountType != AccountType.Liability))
                        {
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The customer deposits account is unavailable.", "Configure an active customer deposits liability account before clearing unallocated receipts.");
                        }
                    }

                    Sale? sale = null;
                    if (payment.SaleId is { } saleId)
                    {
                        sale = await db.Sales
                            .FromSql($"SELECT * FROM sales WHERE id = {saleId} AND business_id = {businessId} FOR UPDATE")
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The receivable sale no longer exists.", "The payment cannot be cleared against a missing sale.");
                        locationId ??= sale.LocationId;
                    }

                    var now = DateTimeOffset.UtcNow;
                    payment.Status = PaymentStatus.Confirmed;
                    payment.ClearedAtUtc = now;
                    payment.BankAccountId = bankAccount.Id;
                    if (invoice is not null)
                    {
                        invoice.AmountPaid = LedgerWriter.Round(invoice.AmountPaid + payment.Amount);
                        invoice.BalanceDue = LedgerWriter.Round(invoice.BalanceDue - payment.Amount);
                        invoice.Status = invoice.BalanceDue == 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
                    }
                    foreach (var allocation in invoiceAllocations)
                    {
                        allocation.Invoice.AmountPaid = LedgerWriter.Round(allocation.Invoice.AmountPaid + allocation.Amount);
                        allocation.Invoice.BalanceDue = LedgerWriter.Round(allocation.Invoice.BalanceDue - allocation.Amount);
                        allocation.Invoice.Status = allocation.Invoice.BalanceDue == 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
                    }
                    if (sale is not null)
                    {
                        sale.PaymentStatus = PaymentStatus.Confirmed;
                    }

                    var paymentLines = new List<JournalLineRequest>
                    {
                        new(bankAccount.Id, payment.Amount, 0m, "Payment cleared to bank", locationId),
                    };
                    if (invoice is not null)
                    {
                        paymentLines.Add(new JournalLineRequest(accounts[ReceivableAccountCode].Id, 0m, payment.Amount, "Clear open receivable", locationId));
                    }
                    else if (invoiceAllocations.Count > 0)
                    {
                        paymentLines.AddRange(invoiceAllocations.Select(allocation =>
                            new JournalLineRequest(accounts[ReceivableAccountCode].Id, 0m, allocation.Amount, $"Clear receipt for {allocation.Invoice.InvoiceNumber}", allocation.Invoice.LocationId)));
                    }
                    else if (!customerDeposit)
                    {
                        paymentLines.Add(new JournalLineRequest(accounts[ReceivableAccountCode].Id, 0m, payment.Amount, "Clear POS receivable", locationId));
                    }
                    if (customerDeposit && payment.UnallocatedAmount > 0)
                    {
                        paymentLines.Add(new JournalLineRequest(accounts["2105"].Id, 0m, payment.UnallocatedAmount, "Clear customer deposit receipt", null));
                    }

                    await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        payment.Currency,
                        LedgerWriter.LocalDate(null, now),
                        $"Clear {payment.Method} payment {payment.Reference}",
                        JournalSourceType.Payment,
                        payment.Id,
                        reversalOfEntryId: null,
                        paymentLines,
                        cancellationToken).ConfigureAwait(false);

                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "payment",
                        EntityId = payment.Id,
                        Action = "cleared",
                        OccurredAtUtc = now,
                        CorrelationId = http.TraceIdentifier,
                        Summary = JsonSerializer.Serialize(new { payment.Method, payment.Amount, payment.Reference, bankAccountId = bankAccount.Id }),
                    });

                    return (StatusCodes.Status200OK, new PaymentTransitionDto(payment.Id, payment.Status.ToString(), now, null, null));
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Json(execution.Body, statusCode: execution.StatusCode);
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

    private static async Task<IResult> BouncePaymentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        PaymentBounceRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            return ApiResults.ValidationProblem("A bounce reason of 1-500 characters is required.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = ["Enter a short reason for the returned payment."],
            });
        }

        var key = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (key.Length is 0 or > 128)
        {
            return ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]>
            {
                [IdempotencyHeader] = ["Send a client-generated key of 1-128 characters."],
            });
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "payment.bounce",
                key,
                new { PaymentId = id, Reason = reason },
                async () =>
                {
                    var payment = await db.Payments
                        .FromSql($"SELECT * FROM payments WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (payment is null)
                    {
                        throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The payment does not exist in this business.", "Check the identifier and signed-in business.");
                    }
                    if (payment.Status != PaymentStatus.Pending
                        || payment.Method is not (PaymentMethod.Cheque or PaymentMethod.BankTransfer))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Only a pending cheque or bank transfer can be bounced.", "Bounce each pending tender once after the return is confirmed.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    payment.Status = PaymentStatus.Bounced;
                    payment.BouncedAtUtc = now;
                    payment.BounceReason = reason;
                    if (payment.SaleId is { } saleId)
                    {
                        var sale = await db.Sales
                            .FromSql($"SELECT * FROM sales WHERE id = {saleId} AND business_id = {businessId} FOR UPDATE")
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (sale is not null)
                        {
                            sale.PaymentStatus = PaymentStatus.Bounced;
                        }
                    }

                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "payment",
                        EntityId = payment.Id,
                        Action = "bounced",
                        OccurredAtUtc = now,
                        CorrelationId = http.TraceIdentifier,
                        Summary = JsonSerializer.Serialize(new { payment.Method, payment.Amount, payment.Reference, reason }),
                    });

                    return (StatusCodes.Status200OK, new PaymentTransitionDto(payment.Id, payment.Status.ToString(), null, now, reason));
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Json(execution.Body, statusCode: execution.StatusCode);
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

    // ---------------------------------------------------------------- queries

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? status,
        Guid? customerId,
        string? search,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!principal.TryGetCurrentUserId(out var listUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.Invoices.AsNoTracking().Where(invoice => invoice.BusinessId == businessId);

        // Location-limited memberships never see another location's invoices, and invoices without a location
        // are visible only to memberships that hold every location.
        var accessibleLocations = await BusinessScope.AccessibleLocationIdsAsync(db, listUserId, businessId, cancellationToken).ConfigureAwait(false);
        if (accessibleLocations is not null)
        {
            var locationIds = accessibleLocations.ToArray();
            query = query.Where(invoice => invoice.LocationId != null && locationIds.Contains(invoice.LocationId.Value));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<InvoiceStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
            {
                return ApiResults.ValidationProblem(
                    "The filter is invalid.",
                    new Dictionary<string, string[]> { ["status"] = [$"Unknown invoice status '{status}'."] });
            }

            query = query.Where(invoice => invoice.Status == parsedStatus);
        }

        if (customerId is { } customer)
        {
            query = query.Where(invoice => invoice.CustomerId == customer);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = search.Trim();
            query = query.Where(invoice => EF.Functions.ILike(invoice.InvoiceNumber, $"%{pattern}%"));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(invoice => invoice.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(invoice => invoice.Id)
            .Take(size + 1)
            .Select(invoice => new InvoiceRow(
                invoice.Id,
                invoice.InvoiceNumber,
                invoice.Status,
                invoice.CustomerId,
                invoice.IssueDate,
                invoice.DueDate,
                invoice.Currency,
                invoice.SubtotalAmount,
                invoice.DiscountAmount,
                invoice.TaxAmount,
                invoice.TotalAmount,
                invoice.AmountPaid,
                invoice.CreditedAmount,
                invoice.BalanceDue,
                invoice.Note,
                invoice.IssuedAtUtc,
                invoice.VoidedAtUtc,
                invoice.Version,
                invoice.RecognitionMode,
                invoice.SourceSaleId,
                invoice.SourceOrderId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var customerIds = rows.Where(row => row.CustomerId is not null).Select(row => row.CustomerId!.Value).Distinct().ToArray();

        var customers = await db.Customers.AsNoTracking()
            .Where(candidate => candidate.BusinessId == businessId && customerIds.Contains(candidate.Id))
            .Select(candidate => new { candidate.Id, candidate.Name })
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.Name, cancellationToken)
            .ConfigureAwait(false);

        var page = rows
            .Select(row => ToSummary(
                row,
                row.CustomerId is { } id && customers.TryGetValue(id, out var name) ? name : null))
            .ToList();

        return Results.Ok(new PagedResult<InvoiceSummaryDto>(page, nextCursor, size));
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

        if (!principal.TryGetCurrentUserId(out var readUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        // An invoice outside the caller's locations is reported as missing: its existence is not disclosed.
        if (invoice is null || !await CanAccessInvoiceLocationAsync(db, readUserId, businessId, invoice.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundInvoice();
        }

        return Results.Ok(await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PrintAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!principal.TryGetCurrentUserId(out var printUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null || !await CanAccessInvoiceLocationAsync(db, printUserId, businessId, invoice.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundInvoice();
        }

        var detail = await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false);
        return Results.Content(InvoicePrintRenderer.RenderHtml(detail), "text/html", System.Text.Encoding.UTF8);
    }

    // ---------------------------------------------------------------- draft lifecycle

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (request.RecognitionMode is null || request.RecognitionMode is InvoiceRecognitionMode.SaleRepresentation or InvoiceRecognitionMode.Delivery or InvoiceRecognitionMode.HistoricalUnknown)
        {
            return InvalidRecognitionMode();
        }

        if (request.SourceSaleId is not null || request.ExpectedVersion is not null)
        {
            return ApiResults.ValidationProblem("A new draft cannot claim an existing source or version.", new Dictionary<string, string[]>
            {
                ["sourceSaleId"] = ["Use the dedicated sale representation operation for an existing posted sale."],
                ["expectedVersion"] = ["A new draft has no prior version."],
            });
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        if (request.LocationId is { } requestedLocation && TryGetUserId(principal, out var createUserId)
            && !await BusinessScope.CanAccessLocationAsync(db, createUserId, businessId, requestedLocation, cancellationToken).ConfigureAwait(false))
            return ForbiddenLocation();

        var prepared = await PrepareAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var lines = prepared.Lines!;
        var modeError = ValidateRecognitionLines(request.RecognitionMode.Value, request.LocationId, lines);
        if (modeError is not null) return modeError;
        var totals = TotalsOf(lines);

        var invoice = new Invoice
        {
            BusinessId = businessId,
            RecognitionMode = request.RecognitionMode.Value.ToString(),
            LocationId = request.LocationId,
            CustomerId = request.CustomerId,
            InvoiceNumber = string.Empty,
            Status = InvoiceStatus.Draft,
            IssueDate = prepared.IssueDate,
            DueDate = request.DueDate,
            Currency = business.BaseCurrency,
            SubtotalAmount = totals.Subtotal,
            DiscountAmount = totals.Discount,
            TaxAmount = totals.Tax,
            TotalAmount = totals.Total,
            AmountPaid = 0m,
            CreditedAmount = 0m,
            BalanceDue = totals.Total,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        };

        db.Invoices.Add(invoice);
        AddLines(db, businessId, invoice.Id, lines);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/invoices/{invoice.Id}",
            await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateFromSaleAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CreateSaleInvoiceRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
            return BusinessScope.MissingMembership();
        if (request.SaleId == Guid.Empty)
            return ApiResults.ValidationProblem("A posted sale is required.", new Dictionary<string, string[]> { ["saleId"] = ["Select the posted sale to represent."] });

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            var result = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var existing = await db.Invoices
                    .FromSql($"SELECT * FROM invoices WHERE business_id = {businessId} AND source_sale_id = {request.SaleId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return (Invoice: existing, Replayed: true);
                }

                var sale = await db.Sales
                    .FromSql($"SELECT * FROM sales WHERE id = {request.SaleId} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (sale is null || sale.Status != SaleStatus.Posted)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "Only a posted, unreversed sale can be represented as an invoice.", "Choose a posted sale from this business; reversed or unresolved sales cannot be billed again.");
                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, sale.LocationId, cancellationToken).ConfigureAwait(false))
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.PermissionDenied,
                        "This sale belongs to a location the user cannot access.", "Request access to the sale location.");

                var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The business is unavailable.", "Verify the active business membership.");
                var saleLines = await db.SaleLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.SaleId == sale.Id)
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (saleLines.Count == 0)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The posted sale has no immutable lines to represent.", "Investigate the sale record before creating a customer document.");

                var customer = sale.CustomerId is { } customerId
                    ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == customerId, cancellationToken).ConfigureAwait(false)
                    : null;
                var paidAmount = await db.Payments.AsNoTracking()
                    .Where(payment => payment.BusinessId == businessId && payment.SaleId == sale.Id && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
                    .SumAsync(payment => (decimal?)payment.Amount, cancellationToken).ConfigureAwait(false) ?? 0m;
                paidAmount = Math.Min(sale.TotalAmount, LedgerWriter.Round(paidAmount));
                var saleLineIds = saleLines.Select(line => line.Id).ToArray();
                var stockedSaleLines = await db.InventoryMovements.AsNoTracking()
                    .Where(movement => movement.BusinessId == businessId && movement.ReferenceType == "sale_line"
                        && saleLineIds.Contains(movement.ReferenceId) && movement.MovementType == MovementType.Sale)
                    .Select(movement => movement.ReferenceId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
                var stockedSaleLineSet = stockedSaleLines.ToHashSet();
                var issueDate = LedgerWriter.LocalDate(business.TimeZone, sale.PostedAtUtc);
                var invoice = new Invoice
                {
                    BusinessId = businessId,
                    RecognitionMode = InvoiceRecognitionMode.SaleRepresentation.ToString(),
                    LocationId = sale.LocationId,
                    CustomerId = sale.CustomerId,
                    SourceSaleId = sale.Id,
                    SourceRequestKey = $"sale:{sale.Id:D}",
                    SourcePayloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"sale:{sale.Id:D}"))).ToLowerInvariant(),
                    InvoiceNumber = await LedgerWriter.AllocateNumberAsync(db, businessId, InvoiceSequenceName, InvoiceSequencePrefix, issueDate.Year, cancellationToken).ConfigureAwait(false),
                    Status = paidAmount >= sale.TotalAmount ? InvoiceStatus.Paid : paidAmount > 0m ? InvoiceStatus.PartiallyPaid : InvoiceStatus.Issued,
                    IssueDate = issueDate,
                    DueDate = customer is null ? null : issueDate.AddDays(Math.Max(0, customer.PaymentTermsDays)),
                    Currency = sale.Currency,
                    SubtotalAmount = sale.SubtotalAmount,
                    DiscountAmount = sale.DiscountAmount,
                    TaxAmount = sale.TaxAmount,
                    TotalAmount = sale.TotalAmount,
                    AmountPaid = paidAmount,
                    CreditedAmount = 0m,
                    BalanceDue = Math.Max(0m, sale.TotalAmount - paidAmount),
                    IssuedAtUtc = sale.PostedAtUtc,
                    BusinessNameSnapshot = business.Name,
                    BusinessLegalNameSnapshot = business.LegalName,
                    CustomerNameSnapshot = customer?.Name,
                    CustomerAddressSnapshot = customer?.Address,
                    CustomerPhoneSnapshot = customer?.Phone,
                    CustomerEmailSnapshot = customer?.Email,
                    PaymentTermsDaysSnapshot = customer?.PaymentTermsDays,
                };
                var invoiceLines = saleLines.Select(line => new InvoiceLine
                {
                    BusinessId = businessId,
                    InvoiceId = invoice.Id,
                    LineNo = line.LineNo,
                    VariantId = line.VariantId,
                    SourceSaleLineId = line.Id,
                    ProductNameSnapshot = line.ProductNameSnapshot,
                    SkuSnapshot = line.SkuSnapshot,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    DiscountAmount = line.DiscountAmount,
                    RevenueBaseAmount = LedgerWriter.Round(line.LineTotal - line.TaxAmount),
                    TaxRate = line.TaxRate,
                    TaxMode = line.TaxMode,
                    TaxAmount = line.TaxAmount,
                    LineTotal = line.LineTotal,
                    UnitCostSnapshot = line.UnitCostSnapshot,
                    TrackStockSnapshot = stockedSaleLineSet.Contains(line.Id),
                }).ToList();
                db.Invoices.Add(invoice);
                db.InvoiceLines.AddRange(invoiceLines);
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "invoice",
                    EntityId = invoice.Id,
                    Action = "represented_sale",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new { saleId = sale.Id, invoiceNumber = invoice.InvoiceNumber, recognitionMode = invoice.RecognitionMode }),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (Invoice: invoice, Replayed: false);
            }).ConfigureAwait(false);

            var detail = await ToDetailAsync(db, businessId, result.Invoice, cancellationToken).ConfigureAwait(false);
            return result.Replayed ? Results.Ok(detail) : Results.Created($"/api/v1/invoices/{result.Invoice.Id}", detail);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
    }

    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        SaveInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var updateUserId)) return BusinessScope.MissingMembership();

        if (!principal.TryGetCurrentUserId(out var locationUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var invoice = await db.Invoices
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null || !await CanAccessInvoiceLocationAsync(db, locationUserId, businessId, invoice.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundInvoice();
        }

        if (invoice.Status != InvoiceStatus.Draft)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.InvoiceNotDraft,
                "Only draft invoices can be edited.",
                "Posted invoices are immutable: corrections are a void (no payments) or a credit note referencing the original.");
        }

        if (request.LocationId is { } requestedLocation
            && !await BusinessScope.CanAccessLocationAsync(db, updateUserId, businessId, requestedLocation, cancellationToken).ConfigureAwait(false))
            return ForbiddenLocation();

        if (request.RecognitionMode is null || request.RecognitionMode is InvoiceRecognitionMode.SaleRepresentation or InvoiceRecognitionMode.Delivery or InvoiceRecognitionMode.HistoricalUnknown
            || request.SourceSaleId is not null)
        {
            return InvalidRecognitionMode();
        }

        var prepared = await PrepareAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var lines = prepared.Lines!;
        var modeError = ValidateRecognitionLines(request.RecognitionMode.Value, request.LocationId, lines);
        if (modeError is not null) return modeError;
        var totals = TotalsOf(lines);

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Draft lines are replaceable; the immutable snapshot is the issued invoice (D-38).
            await db.InvoiceLines
                .Where(line => line.BusinessId == businessId && line.InvoiceId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            var target = await db.Invoices
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            target.LocationId = request.LocationId;
            target.RecognitionMode = request.RecognitionMode.Value.ToString();
            target.CustomerId = request.CustomerId;
            target.IssueDate = prepared.IssueDate;
            target.DueDate = request.DueDate;
            target.SubtotalAmount = totals.Subtotal;
            target.DiscountAmount = totals.Discount;
            target.TaxAmount = totals.Tax;
            target.TotalAmount = totals.Total;
            target.AmountPaid = 0m;
            target.CreditedAmount = 0m;
            target.BalanceDue = totals.Total;
            target.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

            AddLines(db, businessId, id, lines);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var updated = await db.Invoices.AsNoTracking()
            .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(await ToDetailAsync(db, businessId, updated, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> DeleteAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!principal.TryGetCurrentUserId(out var locationUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var invoice = await db.Invoices
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null || !await CanAccessInvoiceLocationAsync(db, locationUserId, businessId, invoice.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundInvoice();
        }

        if (invoice.Status != InvoiceStatus.Draft)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.InvoiceNotDraft,
                "Posted invoices are never deleted.",
                "Void the invoice (no payments) or issue a credit note referencing the original.");
        }

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await db.InvoiceLines
                .Where(line => line.BusinessId == businessId && line.InvoiceId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await db.Invoices
                .Where(candidate => candidate.BusinessId == businessId && candidate.Id == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- issuing, voiding, crediting

    private static async Task<IResult> IssueAsync(
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
            var invoice = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var invoice = await db.Invoices
                    .FromSql($"SELECT * FROM invoices WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (invoice is null || !await CanAccessInvoiceLocationAsync(db, userId, businessId, invoice.LocationId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The invoice does not exist in this business.",
                        "Check the identifier: invoices are scoped to the signed-in business.");
                }

                if (invoice.Status != InvoiceStatus.Draft)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceNotDraft,
                        "Only draft invoices can be issued.",
                        "This invoice was already issued (or voided/credited).");
                }

                if (invoice.LocationId is { } issueLocationId
                    && !await BusinessScope.CanAccessLocationAsync(db, userId, businessId, issueLocationId, cancellationToken).ConfigureAwait(false))
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.PermissionDenied,
                        "This invoice belongs to a location the user cannot access.", "Request access to the invoice location.");

                var lines = await db.InvoiceLines
                    .Where(line => line.BusinessId == businessId && line.InvoiceId == id)
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (lines.Count == 0)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.ValidationFailed,
                        "The draft has no lines to issue.",
                        "Add at least one line before issuing the invoice.");
                }

                var mode = Enum.TryParse<InvoiceRecognitionMode>(invoice.RecognitionMode, out var parsedMode)
                    ? parsedMode
                    : InvoiceRecognitionMode.HistoricalUnknown;
                if (mode is not (InvoiceRecognitionMode.Service or InvoiceRecognitionMode.DirectGoods))
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "This invoice draft has no issuable recognition mode.",
                        "Recreate the draft with an explicit service or direct-goods mode; source-linked invoices use their dedicated workflow.");
                }
                if (mode == InvoiceRecognitionMode.Service && lines.Any(line => line.VariantId is not null)
                    || mode == InvoiceRecognitionMode.DirectGoods && (invoice.LocationId is null || lines.Any(line => line.VariantId is null || !line.TrackStockSnapshot)))
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The invoice lines do not match the selected recognition mode.",
                        "Services cannot consume catalog stock; direct-goods invoices require tracked variants and an active location.");
                }

                var businessSnapshot = await db.Businesses
                    .FromSql($"SELECT * FROM businesses WHERE id = {businessId} FOR SHARE")
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (businessSnapshot is null)
                    throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The business is unavailable.", "Verify the active business membership.");
                Customer? customerSnapshot = null;
                if (invoice.CustomerId is { } snapshotCustomerId)
                {
                    customerSnapshot = await db.Customers
                        .FromSql($"SELECT * FROM customers WHERE id = {snapshotCustomerId} AND business_id = {businessId} FOR SHARE")
                        .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    if (customerSnapshot is null || !customerSnapshot.IsActive)
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The invoice customer is no longer active in this business.", "Select an active customer and save the draft again.");
                }
                BusinessLocation? locationSnapshot = null;
                if (invoice.LocationId is { } snapshotLocationId)
                {
                    locationSnapshot = await db.BusinessLocations
                        .FromSql($"SELECT * FROM business_locations WHERE id = {snapshotLocationId} AND business_id = {businessId} FOR SHARE")
                        .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    if (locationSnapshot is null || !locationSnapshot.IsActive)
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The invoice location is no longer active.", "Choose an active location and save the draft again.");
                }
                invoice.BusinessNameSnapshot = businessSnapshot.Name;
                invoice.BusinessLegalNameSnapshot = businessSnapshot.LegalName;
                invoice.LocationAddressSnapshot = locationSnapshot?.Address;
                invoice.CustomerNameSnapshot = customerSnapshot?.Name;
                invoice.CustomerAddressSnapshot = customerSnapshot?.Address;
                invoice.CustomerPhoneSnapshot = customerSnapshot?.Phone;
                invoice.CustomerEmailSnapshot = customerSnapshot?.Email;
                invoice.PaymentTermsDaysSnapshot = customerSnapshot?.PaymentTermsDays;

                var variantIds = lines.Where(line => line.VariantId.HasValue).Select(line => line.VariantId!.Value).Distinct().ToArray();
                var issueStockSnapshots = await (from variant in db.ProductVariants.AsNoTracking()
                    join product in db.Products.AsNoTracking() on new { variant.ProductId, variant.BusinessId } equals new { ProductId = product.Id, product.BusinessId }
                    where variant.BusinessId == businessId && variantIds.Contains(variant.Id)
                    select new { VariantId = variant.Id, variant.PurchasePrice, variant.Version, product.TrackStock, ProductVersion = product.Version, ProductActive = product.IsActive, VariantActive = variant.IsActive })
                    .ToDictionaryAsync(row => row.VariantId, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (line.VariantId is { } variantId && issueStockSnapshots.TryGetValue(variantId, out var stockSnapshot))
                    {
                        if (!stockSnapshot.ProductActive || !stockSnapshot.VariantActive
                            || line.ProductVersionSnapshot != stockSnapshot.ProductVersion
                            || line.VariantVersionSnapshot != stockSnapshot.Version)
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                                "Catalog terms changed after this invoice draft was saved.", "Refresh product details and save a new draft before issuing.");
                        line.UnitCostSnapshot = LedgerWriter.Round(stockSnapshot.PurchasePrice ?? 0m);
                        line.TrackStockSnapshot = stockSnapshot.TrackStock;
                    }
                }

                if (invoice.CustomerId is { } customerId)
                {
                    await CustomerCreditLimitGuard.EnsureWithinLimitAsync(
                        db,
                        businessId,
                        customerId,
                        invoice.Currency,
                        invoice.BalanceDue,
                        cancellationToken).ConfigureAwait(false);
                }

                var requiredAccounts = mode == InvoiceRecognitionMode.DirectGoods
                    ? new[] { ReceivableAccountCode, RevenueAccountCode, TaxAccountCode, InventoryAccountCode, CogsAccountCode }
                    : new[] { ReceivableAccountCode, RevenueAccountCode, TaxAccountCode };
                var accounts = await LedgerWriter.ResolveAccountsAsync(
                        db,
                        businessId,
                        requiredAccounts,
                        cancellationToken)
                    .ConfigureAwait(false);

                RejectMissingAccounts(accounts, requiredAccounts);

                decimal costOfGoodsSold = 0m;
                if (mode == InvoiceRecognitionMode.DirectGoods)
                {
                    foreach (var line in lines.OrderBy(line => line.VariantId))
                    {
                        var movement = await InventoryLedger.ApplyAsync(
                            db, businessId, userId, invoice.LocationId!.Value, line.VariantId!.Value,
                            MovementType.Sale, -line.Quantity, line.UnitCostSnapshot, "invoice_line", line.Id,
                            $"Invoice draft {invoice.Id}", now, cancellationToken).ConfigureAwait(false);
                        costOfGoodsSold -= movement.ValueDelta;
                        if (line.Quantity > 0m)
                            line.UnitCostSnapshot = LedgerWriter.Round(costOfGoodsSold == 0m ? -movement.ValueDelta / line.Quantity : -movement.ValueDelta / line.Quantity);
                    }
                }

                var number = await LedgerWriter.AllocateNumberAsync(
                        db,
                        businessId,
                        InvoiceSequenceName,
                        InvoiceSequencePrefix,
                        invoice.IssueDate.Year,
                        cancellationToken)
                    .ConfigureAwait(false);

                var journalLines = new List<JournalLineRequest>
                {
                    new(accounts[ReceivableAccountCode].Id, invoice.TotalAmount, 0m, $"Invoice {number}", invoice.LocationId),
                    new(accounts[RevenueAccountCode].Id, 0m, LedgerWriter.Round(lines.Sum(line => line.RevenueBaseAmount)), $"Invoice {number}", invoice.LocationId),
                };

                if (invoice.TaxAmount > 0)
                {
                    journalLines.Add(new JournalLineRequest(accounts[TaxAccountCode].Id, 0m, invoice.TaxAmount, $"Invoice {number} tax", invoice.LocationId));
                }
                if (costOfGoodsSold > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts[CogsAccountCode].Id, costOfGoodsSold, 0m, $"Cost of goods sold {number}", invoice.LocationId));
                    journalLines.Add(new JournalLineRequest(accounts[InventoryAccountCode].Id, 0m, costOfGoodsSold, $"Inventory relief {number}", invoice.LocationId));
                }

                var entry = await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        invoice.Currency,
                        invoice.IssueDate,
                        $"Invoice {number}",
                        JournalSourceType.Invoice,
                        invoice.Id,
                        reversalOfEntryId: null,
                        journalLines,
                        cancellationToken)
                    .ConfigureAwait(false);

                invoice.InvoiceNumber = number;
                invoice.Status = InvoiceStatus.Issued;
                invoice.IssuedAtUtc = now;

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "invoice",
                    EntityId = invoice.Id,
                    Action = "issued",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        invoiceNumber = number,
                        total = invoice.TotalAmount,
                        customerId = invoice.CustomerId,
                        journalEntry = entry.EntryNumber,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return invoice;
            }).ConfigureAwait(false);

            return Results.Created($"/api/v1/invoices/{invoice.Id}", await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> VoidAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        InvoiceVoidRequest request,
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

        if (request is null)
        {
            return ApiResults.ValidationProblem(
                "The void payload is invalid.",
                new Dictionary<string, string[]> { ["request"] = ["A void payload is required."] });
        }

        var reason = request?.Reason?.Trim() ?? string.Empty;

        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem(
                "The void payload is invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"A void reason of 1-{MaxReasonLength} characters is required (audited with the actor and time)."] });
        }

        var note = request?.Note;
        var strategy = db.Database.CreateExecutionStrategy();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var invoice = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var invoice = await db.Invoices
                    .FromSql($"SELECT * FROM invoices WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (invoice is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The invoice does not exist in this business.",
                        "Check the identifier: invoices are scoped to the signed-in business.");
                }

                if (invoice.Status == InvoiceStatus.Draft)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceNotIssued,
                        "Draft invoices are deleted, not voided.",
                        "Delete this draft or issue it first.");
                }

                if (invoice.RecognitionMode != InvoiceRecognitionMode.Service.ToString())
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "This invoice cannot be voided through the service-invoice correction route.",
                        invoice.RecognitionMode == InvoiceRecognitionMode.SaleRepresentation.ToString()
                            ? "Correct the original posted sale using its sales-return workflow."
                            : "Use the source-aware credit or return workflow so inventory and cost effects are reversed exactly once.");
                }

                if (invoice.Status == InvoiceStatus.Void)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "This invoice is already void.",
                        "Voiding is a one-time transition; the original entries remain in the ledger.");
                }

                var collectedOrPending = await db.Payments.AsNoTracking()
                    .Where(payment => payment.BusinessId == businessId
                        && payment.InvoiceId == id
                        && (payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed
                            || payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Pending))
                    .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
                    .ConfigureAwait(false) ?? 0m;

                if (collectedOrPending > 0)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceHasPayments,
                        "An invoice with confirmed or pending payments cannot be voided.",
                        "Clear or bounce pending tenders before voiding, or record a return as a credit note.");
                }

                var entry = await ReverseInvoiceEntryAsync(
                        db, businessId, userId, invoice, $"Void of {invoice.InvoiceNumber}", cancellationToken)
                    .ConfigureAwait(false);

                invoice.Status = InvoiceStatus.Void;
                invoice.CreditedAmount = invoice.TotalAmount;
                invoice.BalanceDue = 0m;
                invoice.VoidedAtUtc = now;

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "invoice",
                    EntityId = invoice.Id,
                    Action = "voided",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        invoiceNumber = invoice.InvoiceNumber,
                        reversalEntry = entry.EntryNumber,
                        reason,
                        note,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return invoice;
            }).ConfigureAwait(false);

            return Results.Ok(await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> CreditAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        InvoiceCreditNoteRequest request,
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

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var payloadHash = RequestFingerprint.Compute(new { InvoiceId = id, Request = request });
        var replay = await db.CreditNotes.AsNoTracking()
            .FirstOrDefaultAsync(note => note.BusinessId == businessId && note.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different credit note payload.",
                    "Retry the original payload with the same key or create a new key for a new credit.");
            }

            var replayInvoice = await db.Invoices.AsNoTracking()
                .FirstAsync(invoice => invoice.BusinessId == businessId && invoice.Id == replay.InvoiceId, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(await ToDetailAsync(db, businessId, replayInvoice, cancellationToken).ConfigureAwait(false));
        }

        var reason = request?.Reason?.Trim() ?? string.Empty;

        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem(
                "The credit note payload is invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"A credit reason of 1-{MaxReasonLength} characters is required."] });
        }

        if (request?.Note is { Length: > MaxNoteLength })
        {
            return ApiResults.ValidationProblem(
                "The credit note payload is invalid.",
                new Dictionary<string, string[]> { ["note"] = [$"A note of at most {MaxNoteLength} characters is supported."] });
        }

        if (request?.Lines is { Count: > 0 } creditLines)
        {
            if (creditLines.Any(line => line.InvoiceLineId == Guid.Empty || line.Quantity <= 0m || decimal.Round(line.Quantity, 6) != line.Quantity))
            {
                return ApiResults.ValidationProblem(
                    "The credit note payload is invalid.",
                    new Dictionary<string, string[]> { ["lines"] = ["Each line needs an invoice line id and a positive quantity with at most six decimal places."] });
            }

            if (creditLines.Select(line => line.InvoiceLineId).Distinct().Count() != creditLines.Count)
            {
                return ApiResults.ValidationProblem(
                    "The credit note payload is invalid.",
                    new Dictionary<string, string[]> { ["lines"] = ["An invoice line may appear at most once in a credit note."] });
            }
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
            var creditNoteOperation = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var invoice = await db.Invoices
                    .FromSql($"SELECT * FROM invoices WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (invoice is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The invoice does not exist in this business.",
                        "Check the identifier: invoices are scoped to the signed-in business.");
                }

                var existingKey = await db.CreditNotes.AsNoTracking()
                    .FirstOrDefaultAsync(note => note.BusinessId == businessId && note.IdempotencyKey == idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (existingKey is not null)
                {
                    if (!string.Equals(existingKey.PayloadHash, payloadHash, StringComparison.Ordinal))
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.SameKeyDifferentPayload,
                            "This idempotency key was used for a different credit note payload.",
                            "Retry the original payload with the same key or create a new key for a new credit.");
                    }

                    return new CreditNoteOperation(existingKey, Replayed: true);
                }

                if (invoice.Status == InvoiceStatus.Draft)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceNotIssued,
                        "Only issued invoices can be credited.",
                        "Draft invoices are simply deleted.");
                }

                if (invoice.RecognitionMode is var recognitionMode
                    && recognitionMode is not (nameof(InvoiceRecognitionMode.Service) or nameof(InvoiceRecognitionMode.DirectGoods)))
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "This source-linked invoice cannot be credited through the generic invoice route.",
                        "Correct its original sale or order delivery through the corresponding return workflow.");
                }

                if (invoice.Status == InvoiceStatus.Void)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "This invoice is void; there is nothing left to credit.",
                        "Void or credited invoices cannot be credited again.");
                }

                var pendingPayments = await db.Payments.AsNoTracking()
                    .Where(payment => payment.BusinessId == businessId
                        && payment.InvoiceId == id
                        && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Pending)
                    .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
                    .ConfigureAwait(false) ?? 0m;
                if (pendingPayments > 0m)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceHasPayments,
                        "An invoice with a pending payment cannot receive a credit note.",
                        "Clear or bounce pending payments before changing the credited invoice balance.");
                }

                var invoiceLines = await db.InvoiceLines
                    .FromSql($"SELECT * FROM invoice_lines WHERE business_id = {businessId} AND invoice_id = {id} ORDER BY line_no FOR UPDATE")
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var priorCreditLines = await (from creditLine in db.CreditNoteLines.AsNoTracking()
                    join priorNote in db.CreditNotes.AsNoTracking() on new { creditLine.CreditNoteId, creditLine.BusinessId } equals new { CreditNoteId = priorNote.Id, priorNote.BusinessId }
                    where priorNote.BusinessId == businessId && priorNote.InvoiceId == id
                    select new PriorCreditLine(creditLine.InvoiceLineId, creditLine.QuantityCredited, creditLine.TaxAmount, creditLine.LineTotal))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var creditedByLine = priorCreditLines.GroupBy(line => line.InvoiceLineId)
                    .ToDictionary(group => group.Key, group => new PriorCreditTotals(
                        group.Sum(line => line.Quantity),
                        group.Sum(line => line.TaxAmount),
                        group.Sum(line => line.LineTotal)));
                var invoiceLineById = invoiceLines.ToDictionary(line => line.Id);
                var requestedLines = request?.Lines is null or { Count: 0 }
                    ? invoiceLines
                        .Where(line => !creditedByLine.TryGetValue(line.Id, out var prior) || prior.Quantity < line.Quantity)
                        .Select(line => new InvoiceCreditLineRequest(
                            line.Id,
                            line.Quantity - (creditedByLine.TryGetValue(line.Id, out var prior) ? prior.Quantity : 0m)))
                        .ToList()
                    : request.Lines;

                var calculatedLines = new List<CalculatedCreditLine>(requestedLines.Count);
                foreach (var requested in requestedLines.OrderBy(line => invoiceLineById.TryGetValue(line.InvoiceLineId, out var matched) ? matched.LineNo : int.MaxValue))
                {
                    if (!invoiceLineById.TryGetValue(requested.InvoiceLineId, out var invoiceLine))
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed,
                            "The credit note lines are invalid.",
                            "Every credit line must refer to a line on this invoice.");
                    }

                    var prior = creditedByLine.GetValueOrDefault(requested.InvoiceLineId) ?? new PriorCreditTotals(0m, 0m, 0m);
                    var remaining = LedgerWriter.Round(invoiceLine.Quantity - prior.Quantity);
                    if (requested.Quantity <= 0m || requested.Quantity > remaining)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed,
                            "The credit note quantities are invalid.",
                            $"The remaining uncredited quantity for invoice line {invoiceLine.LineNo} is {remaining}.");
                    }

                    var amounts = CalculateCreditAmounts(invoiceLine, requested.Quantity, remaining, prior);
                    if (requested.RestockInventory && invoiceLine.TrackStockSnapshot
                        && (invoiceLine.VariantId is null || invoice.LocationId is null))
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed,
                            "The credit note inventory disposition is invalid.",
                            $"Invoice line {invoiceLine.LineNo} has no stock variant or invoice location to restock.");
                    }

                    calculatedLines.Add(new CalculatedCreditLine(
                        invoiceLine,
                        requested.Quantity,
                        requested.RestockInventory,
                        amounts.TaxAmount,
                        amounts.LineTotal));
                }

                if (calculatedLines.Count == 0)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceAlreadyCredited,
                        "The invoice has no remaining quantity to credit.",
                        "All invoice line quantities have already been credited.");
                }

                var creditAmount = LedgerWriter.Round(calculatedLines.Sum(line => line.LineTotal));
                var previouslyCredited = LedgerWriter.Round(priorCreditLines.Sum(line => line.LineTotal));
                if (creditAmount <= 0m || creditAmount > LedgerWriter.Round(invoice.TotalAmount - previouslyCredited))
                {
                    throw new DomainRuleException(
                        StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed,
                        "The credit note amount is invalid.",
                        "The requested amount must be positive and cannot exceed the invoice's remaining amount.");
                }

                var accounts = await LedgerWriter.ResolveAccountsAsync(
                    db,
                    businessId,
                    [ReceivableAccountCode, TaxAccountCode, "4010", "1200", "2105", "5000"],
                    cancellationToken).ConfigureAwait(false);
                RejectMissingAccounts(accounts, ReceivableAccountCode, TaxAccountCode, "4010", "1200", "2105", "5000");

                var creditNumber = await LedgerWriter.AllocateNumberAsync(
                        db,
                        businessId,
                        CreditSequenceName,
                        CreditSequencePrefix,
                        LedgerWriter.LocalDate(business.TimeZone, now).Year,
                        cancellationToken)
                    .ConfigureAwait(false);

                var creditNote = new CreditNote
                {
                    BusinessId = businessId,
                    InvoiceId = invoice.Id,
                    LocationId = invoice.LocationId,
                    CreditNumber = creditNumber,
                    IssueDate = LedgerWriter.LocalDate(business.TimeZone, now),
                    Currency = invoice.Currency,
                    Amount = creditAmount,
                    Reason = reason,
                    Note = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim(),
                    IssuedAtUtc = now,
                    IssuedByUserId = userId,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                var creditNoteLineEntities = new Dictionary<Guid, CreditNoteLine>();
                var lineNo = 1;
                foreach (var calculated in calculatedLines)
                {
                    var creditLine = new CreditNoteLine
                    {
                        BusinessId = businessId,
                        CreditNoteId = creditNote.Id,
                        InvoiceLineId = calculated.InvoiceLine.Id,
                        VariantId = calculated.InvoiceLine.VariantId,
                        LineNo = lineNo++,
                        QuantityCredited = calculated.Quantity,
                        UnitPrice = calculated.InvoiceLine.UnitPrice,
                        TaxAmount = calculated.TaxAmount,
                        LineTotal = calculated.LineTotal,
                        RestockInventory = calculated.RestockInventory,
                        UnitCostRestocked = calculated.RestockInventory && calculated.InvoiceLine.TrackStockSnapshot
                            ? calculated.InvoiceLine.UnitCostSnapshot
                            : 0m,
                    };
                    creditNoteLineEntities.Add(calculated.InvoiceLine.Id, creditLine);
                }

                decimal restockedCost = 0m;
                if (invoice.LocationId is { } locationId)
                {
                    foreach (var calculated in calculatedLines.Where(line => line.RestockInventory && line.InvoiceLine.TrackStockSnapshot)
                        .OrderBy(line => line.InvoiceLine.VariantId))
                    {
                        var movement = await InventoryLedger.ApplyAsync(
                            db,
                            businessId,
                            userId,
                            locationId,
                            calculated.InvoiceLine.VariantId!.Value,
                            MovementType.SaleReversal,
                            calculated.Quantity,
                            calculated.InvoiceLine.UnitCostSnapshot,
                            "credit_note_line",
                            creditNoteLineEntities[calculated.InvoiceLine.Id].Id,
                            $"Credit note {creditNumber}",
                            now,
                            cancellationToken).ConfigureAwait(false);
                        restockedCost = LedgerWriter.Round(restockedCost + movement.ValueDelta);
                    }
                }

                var creditedTax = LedgerWriter.Round(calculatedLines.Sum(line => line.TaxAmount));
                var creditedRevenue = LedgerWriter.Round(creditAmount - creditedTax);
                var creditToReceivable = Math.Min(creditAmount, invoice.BalanceDue);
                var customerCredit = LedgerWriter.Round(creditAmount - creditToReceivable);
                var journalLines = new List<JournalLineRequest>();
                if (creditedRevenue > 0m)
                {
                    journalLines.Add(new(accounts["4010"].Id, creditedRevenue, 0m, "Sales returns", invoice.LocationId));
                }
                if (creditedTax > 0m)
                {
                    journalLines.Add(new(accounts[TaxAccountCode].Id, creditedTax, 0m, "Tax credited", invoice.LocationId));
                }
                if (creditToReceivable > 0m)
                {
                    journalLines.Add(new(accounts[ReceivableAccountCode].Id, 0m, creditToReceivable, $"Credit {creditNumber}", invoice.LocationId));
                }
                if (customerCredit > 0m)
                {
                    journalLines.Add(new(accounts["2105"].Id, 0m, customerCredit, $"Customer credit {creditNumber}", invoice.LocationId));
                }
                if (restockedCost > 0m)
                {
                    journalLines.Add(new(accounts["1200"].Id, restockedCost, 0m, "Restocked inventory", invoice.LocationId));
                    journalLines.Add(new(accounts["5000"].Id, 0m, restockedCost, "COGS reversal", invoice.LocationId));
                }
                var entry = await LedgerWriter.PostWithinTransactionAsync(
                    db,
                    businessId,
                    userId,
                    invoice.Currency,
                    LedgerWriter.LocalDate(business.TimeZone, now),
                    $"Credit note for {invoice.InvoiceNumber}",
                    JournalSourceType.CreditNote,
                    creditNote.Id,
                    reversalOfEntryId: null,
                    journalLines,
                    cancellationToken).ConfigureAwait(false);
                creditNote.JournalEntryId = entry.Id;
                db.CreditNotes.Add(creditNote);
                foreach (var creditLine in creditNoteLineEntities.Values)
                {
                    db.CreditNoteLines.Add(creditLine);
                }

                invoice.CreditedAmount = LedgerWriter.Round(invoice.CreditedAmount + creditAmount);
                invoice.BalanceDue = LedgerWriter.Round(Math.Max(0m, invoice.TotalAmount - invoice.AmountPaid - invoice.CreditedAmount));
                var fullyCredited = invoice.CreditedAmount >= invoice.TotalAmount;
                if (fullyCredited && invoice.AmountPaid == 0m)
                {
                    invoice.Status = InvoiceStatus.Void;
                    invoice.VoidedAtUtc = now;
                }
                else if (invoice.BalanceDue == 0m && invoice.AmountPaid > 0m)
                {
                    invoice.Status = InvoiceStatus.Paid;
                }
                else if (invoice.AmountPaid > 0m)
                {
                    invoice.Status = InvoiceStatus.PartiallyPaid;
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "credit_note",
                    EntityId = creditNote.Id,
                    Action = "issued",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        creditNumber,
                        invoiceNumber = invoice.InvoiceNumber,
                        amount = creditAmount,
                        lines = calculatedLines.Count,
                        reversalEntry = entry.EntryNumber,
                        reason,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new CreditNoteOperation(creditNote, Replayed: false);
            }).ConfigureAwait(false);

            var refreshed = await db.Invoices.AsNoTracking()
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);
            var detail = await ToDetailAsync(db, businessId, refreshed, cancellationToken).ConfigureAwait(false);

            if (creditNoteOperation.Replayed)
            {
                return Results.Ok(detail);
            }

            return Results.Created(
                $"/api/v1/invoices/{id}/credit-notes/{creditNoteOperation.CreditNote.Id}",
                detail);
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

    // ---------------------------------------------------------------- payment allocation

    private static async Task<IResult> AddPaymentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        IPaymentTerminalProvider terminalProvider,
        Guid id,
        InvoicePaymentRequest request,
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

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();

        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var payloadHash = RequestFingerprint.Compute(request);

        var existingPayment = await db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.BusinessId == businessId && payment.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existingPayment is not null)
        {
            if (!string.Equals(existingPayment.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different payload.",
                    $"Key '{idempotencyKey}' already allocated a payment; start a new request instead of changing the payload.");
            }

            var replayedInvoice = await db.Invoices.AsNoTracking()
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == existingPayment.InvoiceId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new InvoicePaymentResult(
                ToPaymentDto(existingPayment),
                await ToSummaryAsync(db, businessId, replayedInvoice, cancellationToken).ConfigureAwait(false)));
        }

        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null)
        {
            return NotFoundInvoice();
        }

        if (invoice.Status is InvoiceStatus.Draft)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.InvoiceNotIssued,
                "Draft invoices cannot receive payments.",
                "Issue the invoice first.");
        }

        if (invoice.Status is InvoiceStatus.Void)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This invoice is void and cannot receive payments.");
        }

        if (!Enum.TryParse<PaymentMethod>(request.Method?.Trim(), ignoreCase: true, out var method)
            || method is PaymentMethod.CustomerCredit)
        {
            return ApiResults.ValidationProblem(
                "The payment payload is invalid.",
                new Dictionary<string, string[]> { ["method"] = ["Use 'cash', 'cheque', 'bankTransfer' or a verified 'card' capture for invoice payments."] });
        }

        var amount = LedgerWriter.Round(request.Amount);

        if (request.Amount <= 0 || amount != request.Amount)
        {
            return ApiResults.ValidationProblem(
                "The payment payload is invalid.",
                new Dictionary<string, string[]> { ["amount"] = [$"A positive amount of at most {MoneyScale} decimal places is required."] });
        }

        var reference = request.Reference?.Trim();

        if (reference is { Length: > 128 })
            return ApiResults.ValidationProblem("The payment payload is invalid.",
                new Dictionary<string, string[]> { ["reference"] = ["A payment reference is limited to 128 characters."] });

        if ((method is PaymentMethod.Cheque or PaymentMethod.BankTransfer && reference is null or { Length: 0 })
            || method == PaymentMethod.Card && string.IsNullOrWhiteSpace(reference))
        {
            return ApiResults.ValidationProblem(
                "The payment payload is invalid.",
                new Dictionary<string, string[]> { ["reference"] = ["Cheque, bank transfer, and card payments require a processing reference."] });
        }

        TerminalCaptureVerification? terminalCapture = null;
        if (method == PaymentMethod.Card)
        {
            if (invoice.LocationId is not { } invoiceLocationId)
                return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.PaymentUnconfirmed,
                    "The card payment is not confirmed.", "The invoice must have a location before a terminal capture can be verified.");

            var capture = await PaymentTerminalEndpoints.VerifyCaptureAsync(
                provider: terminalProvider,
                businessId: businessId,
                locationId: invoiceLocationId,
                expectedAmount: amount,
                currency: invoice.Currency,
                pspReference: reference,
                capture: request.TerminalCapture,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (capture.Error is not null) return capture.Error;
            terminalCapture = capture.Verification;
        }
        else if (request.TerminalCapture is not null)
        {
            return ApiResults.ValidationProblem("The payment payload is invalid.",
                new Dictionary<string, string[]> { ["terminalCapture"] = ["A PSP capture payload is only valid for a card payment."] });
        }

        var registerContext = await ResolveRegisterAsync(db, businessId, invoice, request.RegisterId, cancellationToken).ConfigureAwait(false);

        if (registerContext.Error is not null)
        {
            return registerContext.Error;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var payment = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var lockedInvoice = await db.Invoices
                    .FromSql($"SELECT * FROM invoices WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (lockedInvoice is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The invoice does not exist in this business.",
                        "Check the identifier: invoices are scoped to the signed-in business.");
                }

                if (lockedInvoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        lockedInvoice.Status is InvoiceStatus.Void ? ApiErrorCodes.Conflict : ApiErrorCodes.InvoiceNotIssued,
                        "This invoice cannot receive payments in its current state.",
                        lockedInvoice.Status is InvoiceStatus.Void
                            ? "A void invoice has no outstanding balance."
                            : "Issue the invoice before allocating payments.");
                }

                var pendingDirect = await db.Payments
                    .Where(candidate => candidate.BusinessId == businessId
                        && candidate.InvoiceId == id
                        && candidate.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Pending)
                    .SumAsync(candidate => (decimal?)candidate.Amount, cancellationToken)
                    .ConfigureAwait(false) ?? 0m;

                var pendingAllocations = await (
                    from allocation in db.PaymentAllocations
                    join candidate in db.Payments on new { allocation.PaymentId, allocation.BusinessId } equals new { PaymentId = candidate.Id, candidate.BusinessId }
                    where allocation.BusinessId == businessId
                        && allocation.InvoiceId == id
                        && candidate.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Pending
                    select (decimal?)allocation.AllocatedAmount)
                    .SumAsync(cancellationToken)
                    .ConfigureAwait(false) ?? 0m;

                var remaining = LedgerWriter.Round(lockedInvoice.BalanceDue - pendingDirect - pendingAllocations);

                if (amount > remaining)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed,
                        "The payment exceeds the open balance of the invoice.",
                        $"Open balance is {remaining.ToString(CultureInfo.InvariantCulture)}; a credit note or a correction is required for anything above it.");
                }

                CashRegisterSession? lockedSession = null;

                if (registerContext.Session is { } session)
                {
                    lockedSession = await db.CashRegisterSessions
                        .FromSql($"SELECT * FROM cash_register_sessions WHERE id = {session.SessionId} AND business_id = {businessId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (lockedSession is null || lockedSession.Status != CashRegisterSessionStatus.Open)
                    {
                        throw new DomainRuleException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.CashRegisterNotOpen,
                            "The drawer session was closed.",
                            "Open a new session before taking cash payments.");
                    }
                }

                var accounts = await LedgerWriter.ResolveAccountsAsync(
                        db,
                        businessId,
                        [ReceivableAccountCode, CashAccountCode, BankAccountCode],
                        cancellationToken)
                    .ConfigureAwait(false);

                RejectMissingAccounts(accounts, ReceivableAccountCode, CashAccountCode, BankAccountCode);

                var debitAccount = method is PaymentMethod.Cash ? accounts[CashAccountCode] : accounts[BankAccountCode];

                var payment = new Payment
                {
                    BusinessId = businessId,
                    InvoiceId = lockedInvoice.Id,
                    Method = method,
                    Amount = amount,
                    Currency = lockedInvoice.Currency,
                    Status = method is PaymentMethod.Cash or PaymentMethod.Card
                        ? QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed
                        : QtsBilling.Persistence.Postgres.PaymentStatus.Pending,
                    Reference = method == PaymentMethod.Card ? null : reference,
                    PspReference = terminalCapture?.PspReference,
                    CardBrand = terminalCapture?.CardBrand,
                    CardLast4 = terminalCapture?.CardLast4,
                    ReceivedAtUtc = now,
                    CreatedByUserId = userId,
                    CashRegisterSessionId = lockedSession?.Id,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                db.Payments.Add(payment);

                if (method is PaymentMethod.Cash or PaymentMethod.Card)
                {
                    await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        lockedInvoice.Currency,
                        LedgerWriter.LocalDate(null, now),
                        $"Payment for {lockedInvoice.InvoiceNumber}",
                        JournalSourceType.Payment,
                        payment.Id,
                        reversalOfEntryId: null,
                        [
                            new JournalLineRequest(debitAccount.Id, amount, 0m, $"Payment for {lockedInvoice.InvoiceNumber}", lockedInvoice.LocationId),
                            new JournalLineRequest(accounts[ReceivableAccountCode].Id, 0m, amount, $"Payment for {lockedInvoice.InvoiceNumber}", lockedInvoice.LocationId),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                }

                if (method is PaymentMethod.Cash && lockedSession is not null)
                {
                    db.CashRegisterMovements.Add(new CashRegisterMovement
                    {
                        BusinessId = businessId,
                        CashRegisterId = lockedSession.CashRegisterId,
                        CashRegisterSessionId = lockedSession.Id,
                        MovementType = CashMovementType.CashIn,
                        Amount = amount,
                        Currency = lockedInvoice.Currency,
                        Reason = $"Invoice {lockedInvoice.InvoiceNumber} payment",
                        CreatedByUserId = userId,
                        OccurredAtUtc = now,
                    });
                }

                if (payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
                {
                    lockedInvoice.AmountPaid = LedgerWriter.Round(lockedInvoice.AmountPaid + amount);
                    lockedInvoice.BalanceDue = LedgerWriter.Round(lockedInvoice.BalanceDue - amount);
                    lockedInvoice.Status = lockedInvoice.BalanceDue == 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "invoice",
                    EntityId = lockedInvoice.Id,
                    Action = "payment_allocated",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        invoiceNumber = lockedInvoice.InvoiceNumber,
                        amount,
                        method = method.ToString(),
                        reference,
                        amountPaid = lockedInvoice.AmountPaid,
                        balanceDue = lockedInvoice.BalanceDue,
                        status = lockedInvoice.Status.ToString(),
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return payment;
            }).ConfigureAwait(false);

            var refreshed = await db.Invoices.AsNoTracking()
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/invoices/{id}/payments/{payment.Id}",
                new InvoicePaymentResult(
                    ToPaymentDto(payment),
                    await ToSummaryAsync(db, businessId, refreshed, cancellationToken).ConfigureAwait(false)));
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

    // ---------------------------------------------------------------- shared helpers

    private static async Task<PreparedInvoice> PrepareAsync(
        BillingDbContext db,
        Guid businessId,
        SaveInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.CustomerId is null)
        {
            errors["customerId"] = ["An invoice requires a customer (the receivable is tracked per customer)."];
        }
        else
        {
            var customerExists = await db.Customers.AsNoTracking()
                .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id == request.CustomerId && candidate.IsActive, cancellationToken)
                .ConfigureAwait(false);

            if (!customerExists)
            {
                errors["customerId"] = ["The customer does not exist or is inactive in this business."];
            }
        }

        if (request.LocationId is { } locationId)
        {
            var locationExists = await db.BusinessLocations.AsNoTracking()
                .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id == locationId && candidate.IsActive, cancellationToken)
                .ConfigureAwait(false);

            if (!locationExists)
            {
                errors["locationId"] = ["The location does not exist or is inactive in this business."];
            }
        }

        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines)
        {
            errors["lines"] = [$"An invoice requires 1-{MaxLines} lines."];
        }

        if (request.DueDate is { } dueDate && dueDate < request.IssueDate)
        {
            errors["dueDate"] = ["The due date cannot precede the issue date."];
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            errors["note"] = [$"A note of at most {MaxNoteLength} characters is supported."];
        }

        var lines = new List<PricedLine>();

        if (request.Lines is { Count: > 0 })

        {
            var lineNo = 1;

            foreach (var line in request.Lines)
            {
                var key = $"lines[{lineNo - 1}]";

                if (line.Quantity <= 0 || line.Quantity > 1_000_000_000m)
                {
                    errors[$"{key}.quantity"] = ["A positive quantity is required."];
                }

                if (line.UnitPrice < 0 || LedgerWriter.Round(line.UnitPrice) != line.UnitPrice)
                {
                    errors[$"{key}.unitPrice"] = [$"A non-negative unit price of at most {MoneyScale} decimal places is required."];
                }

                var gross = LedgerWriter.Round(line.Quantity * line.UnitPrice);
                var net = LedgerWriter.Round(gross - Math.Max(0m, line.DiscountAmount));
                var tax = LedgerWriter.Round(net * line.TaxRate);

                if (line.DiscountAmount < 0 || line.DiscountAmount > gross)
                {
                    errors[$"{key}.discountAmount"] = ["A discount between zero and the line gross amount is required."];
                }

                if (line.TaxRate < 0 || line.TaxRate > 1)
                {
                    errors[$"{key}.taxRate"] = ["The tax rate is a fraction between 0 and 1."];
                }

                var name = line.ProductName?.Trim();
                string? sku = null;
                var unitCost = 0m;
                var trackStock = false;

                if (line.VariantId is { } variantId)
                {
                    var variant = await db.ProductVariants.AsNoTracking()
                        .Where(candidate => candidate.BusinessId == businessId && candidate.Id == variantId)
                        .Select(candidate => new { candidate.Id, candidate.ProductId, candidate.Sku, candidate.IsActive, candidate.PurchasePrice, candidate.Version })
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (variant is null || !variant.IsActive)
                    {
                        errors[$"{key}.variantId"] = ["The variant does not exist or is inactive in this business."];
                    }
                    else
                    {
                        var product = await db.Products.AsNoTracking()
                            .Where(candidate => candidate.BusinessId == businessId && candidate.Id == variant.ProductId)
                            .Select(candidate => new { candidate.Name, candidate.IsActive, candidate.TrackStock, candidate.Version })
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (product is null || !product.IsActive)
                        {
                            errors[$"{key}.variantId"] = ["The product of this variant is inactive."];
                        }
                        else
                        {
                            name = product.Name;
                            sku = variant.Sku;
                            unitCost = LedgerWriter.Round(variant.PurchasePrice ?? 0m);
                            trackStock = product.TrackStock;
                            lines.Add(new PricedLine(line.VariantId, name, sku, line.Quantity, line.UnitPrice, Math.Max(0m, line.DiscountAmount), line.TaxRate, tax, LedgerWriter.Round(net + tax), unitCost, trackStock, product.Version, variant.Version));
                            lineNo++;
                            continue;
                        }
                    }
                }
                else if (name is null or { Length: 0 } || name.Length > MaxNameLength)
                {
                    errors[$"{key}.productName"] = [$"A line without a variant requires a product name of 1-{MaxNameLength} characters."];
                }

                lines.Add(new PricedLine(
                    line.VariantId,
                    name ?? string.Empty,
                    sku,
                    line.Quantity,
                    line.UnitPrice,
                    Math.Max(0, line.DiscountAmount),
                    line.TaxRate,
                    tax,
                    LedgerWriter.Round(net + tax),
                    unitCost,
                    trackStock,
                    null,
                    null));

                lineNo++;
            }
        }

        if (errors.Count > 0)
        {
            return new PreparedInvoice(ApiResults.ValidationProblem("The invoice payload is invalid.", errors), null, default);
        }

        var issueDate = request.IssueDate == default ? DateOnly.FromDateTime(DateTime.UtcNow) : request.IssueDate;

        return new PreparedInvoice(null, lines, issueDate);
    }

    private static IResult? ValidateRecognitionLines(InvoiceRecognitionMode mode, Guid? locationId, IReadOnlyList<PricedLine> lines)
    {
        var valid = mode switch
        {
            InvoiceRecognitionMode.Service => lines.All(line => line.VariantId is null),
            InvoiceRecognitionMode.DirectGoods => locationId is not null && lines.All(line => line.VariantId is not null && line.TrackStockSnapshot),
            _ => false,
        };
        return valid ? null : ApiResults.ValidationProblem(
            "Invoice lines do not match the selected recognition mode.",
            new Dictionary<string, string[]>
            {
                ["recognitionMode"] = [mode == InvoiceRecognitionMode.Service
                    ? "Service invoices must contain only ad-hoc, non-stock lines."
                    : "Direct-goods invoices require tracked variants and an active location."],
            });
    }

    private static IResult InvalidRecognitionMode()
        => ApiResults.ValidationProblem("An explicit invoice recognition mode is required.", new Dictionary<string, string[]>
        {
            ["recognitionMode"] = ["Choose Service or DirectGoods. Sale and delivery representations use their source workflows."],
        });

    private static IResult ForbiddenLocation()
        => ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this location.");

    private static async Task<bool> CanAccessInvoiceLocationAsync(
        BillingDbContext db,
        Guid userId,
        Guid businessId,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        var accessible = await BusinessScope.AccessibleLocationIdsAsync(db, userId, businessId, cancellationToken).ConfigureAwait(false);
        return accessible is null || (locationId is { } location && accessible.Contains(location));
    }

    private static void AddLines(BillingDbContext db, Guid businessId, Guid invoiceId, IReadOnlyList<PricedLine> lines)
    {
        var lineNo = 1;

        foreach (var line in lines)
        {
            db.InvoiceLines.Add(new InvoiceLine
            {
                BusinessId = businessId,
                InvoiceId = invoiceId,
                LineNo = lineNo++,
                VariantId = line.VariantId,
                ProductNameSnapshot = line.ProductName,
                SkuSnapshot = line.Sku,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountAmount = line.DiscountAmount,
                RevenueBaseAmount = LedgerWriter.Round(line.Quantity * line.UnitPrice - line.DiscountAmount),
                TaxMode = "Exclusive",
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                LineTotal = line.LineTotal,
                UnitCostSnapshot = line.UnitCostSnapshot,
                TrackStockSnapshot = line.TrackStockSnapshot,
                ProductVersionSnapshot = line.ProductVersion,
                VariantVersionSnapshot = line.VariantVersion,
            });
        }
    }

    private static InvoiceTotals TotalsOf(IReadOnlyList<PricedLine> lines)
    {
        var subtotal = LedgerWriter.Round(lines.Sum(line => LedgerWriter.Round(line.Quantity * line.UnitPrice)));
        var discount = LedgerWriter.Round(lines.Sum(line => line.DiscountAmount));
        var tax = LedgerWriter.Round(lines.Sum(line => line.TaxAmount));
        var total = LedgerWriter.Round(lines.Sum(line => line.LineTotal));

        return new InvoiceTotals(subtotal, discount, tax, total);
    }

    private static async Task<JournalEntry> ReverseInvoiceEntryAsync(
        BillingDbContext db,
        Guid businessId,
        Guid userId,
        Invoice invoice,
        string memo,
        CancellationToken cancellationToken)
    {
        var original = await db.JournalEntries.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.BusinessId == businessId
                && entry.SourceType == JournalSourceType.Invoice
                && entry.SourceId == invoice.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (original is null)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "The invoice has no posted journal entry to reverse.",
                "Every issued invoice must have a source journal entry; this is a data-integrity error to investigate.");
        }

        var alreadyReversed = await db.JournalEntries.AsNoTracking()
            .AnyAsync(entry => entry.BusinessId == businessId
                && entry.SourceType == JournalSourceType.Reversal
                && entry.SourceId == original.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (alreadyReversed)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "The invoice journal entry already has a reversal.",
                "A posted entry can be reversed at most once.");
        }

        var originalLines = await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntryId == original.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new JournalLineRequest(line.AccountId, line.CreditAmount, line.DebitAmount, line.Description, line.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await LedgerWriter.PostWithinTransactionAsync(
                db,
                businessId,
                userId,
                invoice.Currency,
                LedgerWriter.LocalDate(null, DateTimeOffset.UtcNow),
                memo,
                JournalSourceType.Reversal,
                original.Id,
                reversalOfEntryId: original.Id,
                originalLines,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static (decimal TaxAmount, decimal LineTotal) CalculateCreditAmounts(
        InvoiceLine invoiceLine,
        decimal requestedQuantity,
        decimal remainingQuantity,
        PriorCreditTotals prior)
    {
        if (requestedQuantity == remainingQuantity)
        {
            return (
                LedgerWriter.Round(invoiceLine.TaxAmount - prior.TaxAmount),
                LedgerWriter.Round(invoiceLine.LineTotal - prior.LineTotal));
        }

        // A partial credit follows the tax composition the line was issued with; it is never recomputed as Exclusive.
        var taxMode = CommercialAmountCalculator.ParseTaxMode(invoiceLine.TaxMode, "lines");
        var discount = LedgerWriter.Round(invoiceLine.DiscountAmount * requestedQuantity / invoiceLine.Quantity);
        if (taxMode == QtsModels.ValueObjects.CommercialTaxMode.Exclusive && invoiceLine.TaxRate == 1m)
        {
            // Invoice snapshots accept a 100% exclusive tax rate, while the shared commercial
            // calculator deliberately supports rates below 100% only. Preserve the issued invoice's
            // accepted arithmetic when crediting a partial quantity from that historical snapshot.
            var net = LedgerWriter.Round(requestedQuantity * invoiceLine.UnitPrice - discount);
            var tax = LedgerWriter.Round(net * invoiceLine.TaxRate);
            return (tax, LedgerWriter.Round(net + tax));
        }

        var amounts = CommercialAmountCalculator.ComputeLine(
            requestedQuantity,
            invoiceLine.UnitPrice,
            taxMode,
            invoiceLine.TaxRate,
            discount,
            field: "lines");
        return (amounts.TaxAmount, amounts.PayableAmount);
    }

    private static async Task<RegisterContext> ResolveRegisterAsync(
        BillingDbContext db,
        Guid businessId,
        Invoice invoice,
        Guid? registerId,
        CancellationToken cancellationToken)
    {
        if (registerId is not { } id)
        {
            return new RegisterContext(null, null);
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (register is null || !register.IsActive || (invoice.LocationId is { } locationId && register.LocationId != locationId))
        {
            return new RegisterContext(
                ApiResults.ValidationProblem(
                    "The payment payload is invalid.",
                    new Dictionary<string, string[]> { ["registerId"] = ["The register must be active and belong to the invoice location."] }),
                null);
        }

        var session = await db.CashRegisterSessions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId
                && candidate.CashRegisterId == id
                && candidate.Status == CashRegisterSessionStatus.Open,
                cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return new RegisterContext(
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.CashRegisterNotOpen,
                    "The register has no open drawer session.",
                    "Open a drawer session before taking cash payments."),
                null);
        }

        return new RegisterContext(null, new RegisterSession(register.Id, session.Id));
    }

    private static void RejectMissingAccounts(IReadOnlyDictionary<string, Account> accounts, params string[] codes)
    {
        var missing = codes.Where(code => !accounts.ContainsKey(code)).ToArray();

        if (missing.Length > 0)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                $"The chart of accounts is missing: {string.Join(", ", missing)}.",
                "Seed the standard chart of accounts before posting invoices.");
        }
    }

    internal static async Task<InvoiceDetailDto> ToDetailAsync(
        BillingDbContext db,
        Guid businessId,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.InvoiceId == invoice.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new InvoiceLineDto(
                line.Id,
                line.LineNo,
                line.VariantId,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.TaxRate,
                line.TaxAmount,
                line.LineTotal,
                line.TaxMode,
                line.RevenueBaseAmount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId && payment.InvoiceId == invoice.Id)
            .OrderBy(payment => payment.ReceivedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var creditNotes = await db.CreditNotes.AsNoTracking()
            .Where(note => note.BusinessId == businessId && note.InvoiceId == invoice.Id)
            .OrderBy(note => note.IssuedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var creditDtos = new List<InvoiceCreditNoteDto>(creditNotes.Count);
        foreach (var creditNote in creditNotes)
        {
            string? journalNumber = null;

            if (creditNote.JournalEntryId is { } entryId)
            {
                journalNumber = await db.JournalEntries.AsNoTracking()
                    .Where(entry => entry.Id == entryId)
                    .Select(entry => entry.EntryNumber)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var creditLines = await db.CreditNoteLines.AsNoTracking()
                .Where(line => line.BusinessId == businessId && line.CreditNoteId == creditNote.Id)
                .OrderBy(line => line.LineNo)
                .Select(line => new InvoiceCreditNoteLineDto(
                    line.InvoiceLineId,
                    line.LineNo,
                    line.VariantId,
                    line.QuantityCredited,
                    line.UnitPrice,
                    line.TaxAmount,
                    line.LineTotal,
                    line.RestockInventory,
                    line.UnitCostRestocked))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            creditDtos.Add(new InvoiceCreditNoteDto(
                creditNote.Id,
                creditNote.CreditNumber,
                creditNote.Amount,
                creditNote.Reason,
                creditNote.Note,
                creditNote.IssuedAtUtc,
                journalNumber,
                creditLines));
        }

        var creditDto = creditDtos.LastOrDefault();

        return new InvoiceDetailDto(
            await ToSummaryAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false),
            lines,
            payments.Select(ToPaymentDto).ToList(),
            creditDto,
            creditDtos,
            invoice.LocationId,
            new InvoicePartySnapshotDto(
                invoice.BusinessNameSnapshot ?? "Historical business name unavailable",
                invoice.BusinessLegalNameSnapshot,
                invoice.LocationAddressSnapshot,
                invoice.CustomerNameSnapshot,
                invoice.CustomerAddressSnapshot,
                invoice.CustomerPhoneSnapshot,
                invoice.CustomerEmailSnapshot,
                invoice.PaymentTermsDaysSnapshot));
    }

    private static async Task<InvoiceSummaryDto> ToSummaryAsync(
        BillingDbContext db,
        Guid businessId,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        string? customerName = null;

        if (invoice.CustomerId is { } customerId)
        {
            customerName = await db.Customers.AsNoTracking()
                .Where(candidate => candidate.BusinessId == businessId && candidate.Id == customerId)
                .Select(candidate => candidate.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return ToSummary(
            new InvoiceRow(
                invoice.Id,
                invoice.InvoiceNumber,
                invoice.Status,
                invoice.CustomerId,
                invoice.IssueDate,
                invoice.DueDate,
                invoice.Currency,
                invoice.SubtotalAmount,
                invoice.DiscountAmount,
                invoice.TaxAmount,
                invoice.TotalAmount,
                invoice.AmountPaid,
                invoice.CreditedAmount,
                invoice.BalanceDue,
                invoice.Note,
                invoice.IssuedAtUtc,
                invoice.VoidedAtUtc,
                invoice.Version,
                invoice.RecognitionMode,
                invoice.SourceSaleId,
                invoice.SourceOrderId),
            customerName);
    }

    private static InvoiceSummaryDto ToSummary(InvoiceRow row, string? customerName)
        => new(
            row.Id,
            string.IsNullOrEmpty(row.InvoiceNumber) ? null : row.InvoiceNumber,
            EnumBridge.To<InvoiceState>(row.Status),
            row.CustomerId,
            customerName,
            row.IssueDate,
            row.DueDate,
            row.Currency,
            row.SubtotalAmount,
            row.DiscountAmount,
            row.TaxAmount,
            row.TotalAmount,
            row.AmountPaid,
            row.CreditedAmount,
            row.BalanceDue,
            row.Note,
            row.IssuedAtUtc,
            row.VoidedAtUtc,
            row.Version,
            Enum.TryParse<InvoiceRecognitionMode>(row.RecognitionMode, ignoreCase: false, out var mode)
                ? mode
                : InvoiceRecognitionMode.HistoricalUnknown,
            row.SourceSaleId,
            row.SourceOrderId);

    private static InvoicePaymentDto ToPaymentDto(Payment payment)
        => new(
            payment.Id,
            payment.Method.ToString(),
            EnumBridge.To<QtsBilling.Contracts.Sales.PaymentStatus>(payment.Status).ToString(),
            payment.Amount,
            payment.Currency,
            payment.Reference,
            payment.ReceivedAtUtc,
            payment.PspReference,
            payment.CardBrand,
            payment.CardLast4);

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult NotFoundInvoice()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The invoice does not exist in this business.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_invoices_business_number" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.DuplicateInvoiceNumber,
                    "A concurrent request allocated the same invoice number.",
                    "Retry the issue: the next number is allocated by the locked sequence."),
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_payments_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same key committed first.",
                    "Retry the same payload: the stored payment will be returned instead of charging twice."),
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_credit_notes_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same credit-note key committed first.",
                    "Retry the same payload with the same key to retrieve its result."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The invoice write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The invoice write violates an integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The invoice references a record outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The invoice operation failed."),
        };

    // ---------------------------------------------------------------- local shapes

    private sealed record PricedLine(
        Guid? VariantId,
        string ProductName,
        string? Sku,
        decimal Quantity,
        decimal UnitPrice,
        decimal DiscountAmount,
        decimal TaxRate,
        decimal TaxAmount,
        decimal LineTotal,
        decimal UnitCostSnapshot,
        bool TrackStockSnapshot,
        long? ProductVersion,
        long? VariantVersion);

    private sealed record InvoiceTotals(decimal Subtotal, decimal Discount, decimal Tax, decimal Total);

    private sealed record PriorCreditLine(Guid InvoiceLineId, decimal Quantity, decimal TaxAmount, decimal LineTotal);

    private sealed record PriorCreditTotals(decimal Quantity, decimal TaxAmount, decimal LineTotal);

    private sealed record CalculatedCreditLine(
        InvoiceLine InvoiceLine,
        decimal Quantity,
        bool RestockInventory,
        decimal TaxAmount,
        decimal LineTotal);

    private sealed record CreditNoteOperation(CreditNote CreditNote, bool Replayed);

    private sealed record PreparedInvoice(IResult? Error, IReadOnlyList<PricedLine>? Lines, DateOnly IssueDate);

    private sealed record InvoiceRow(
        Guid Id,
        string InvoiceNumber,
        InvoiceStatus Status,
        Guid? CustomerId,
        DateOnly IssueDate,
        DateOnly? DueDate,
        string Currency,
        decimal SubtotalAmount,
        decimal DiscountAmount,
        decimal TaxAmount,
        decimal TotalAmount,
        decimal AmountPaid,
        decimal CreditedAmount,
        decimal BalanceDue,
        string? Note,
        DateTimeOffset? IssuedAtUtc,
        DateTimeOffset? VoidedAtUtc,
        long Version,
        string RecognitionMode,
        Guid? SourceSaleId,
        Guid? SourceOrderId);

    private sealed record RegisterSession(Guid RegisterId, Guid SessionId);

    private sealed record RegisterContext(IResult? Error, RegisterSession? Session);
}
