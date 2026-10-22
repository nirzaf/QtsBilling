using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Sales;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Invoices;

/// <summary>Customer receipts, multi-invoice allocation and use of customer deposit balances.</summary>
public static class PaymentEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MoneyScale = 4;

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/customers/{customerId:guid}/payments", CreateCustomerPaymentAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.InvoicesIssue);
        app.MapPost("/api/v1/payments/{id:guid}/allocate", AllocateCustomerPaymentAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.AccountingPost);

        // Receivables reads and the deposit refund (specification task 25, recipe steps 1, 4 and 5). The statement
        // and the open-item list answer from the posted events as at a business date; refunding unapplied money is
        // a ledger action, so it needs the posting authority and a stable command key like every other one.
        app.MapGet("/api/v1/customers/{customerId:guid}/statement", CustomerStatementAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.InvoicesIssue);
        app.MapGet("/api/v1/customers/{customerId:guid}/open-items", CustomerOpenItemsAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.InvoicesIssue);
        app.MapPost("/api/v1/payments/{id:guid}/refund-unallocated", RefundUnallocatedAsync)
            .WithTags("Payments")
            .RequireAuthorization(Permissions.AccountingPost);
        return app;
    }

    /// <summary>
    /// Validates the targets a receipt is being applied to. Each entry names exactly one settleable document - an
    /// invoice or an opening receivable - because an application that named both would count one lot of money
    /// twice, and one that named neither would take money off the deposit and settle nothing at all.
    /// </summary>
    private static IResult? ValidateTargets(AllocationTarget[] allocations, string field)
    {
        if (allocations.Length > 100
            || allocations.Any(item => item.Amount <= 0 || Round(item.Amount) != item.Amount)
            || allocations.Any(item => (item.InvoiceId is null) == (item.OpenItemId is null))
            || allocations.Select(item => item.InvoiceId ?? item.OpenItemId!.Value).Distinct().Count() != allocations.Length)
        {
            return ApiResults.ValidationProblem(
                "Every application must name exactly one document - an invoice or an opening receivable - with a positive amount of at most four decimal places.",
                new Dictionary<string, string[]>
                {
                    [field] = ["Remove entries that name two documents or none, remove duplicates, and enter a positive amount for each target."],
                });
        }

        return null;
    }

    private static async Task<IResult> CreateCustomerPaymentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid customerId,
        CreateCustomerPaymentRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!TryGetScope(principal, out var businessId, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!Enum.TryParse<PaymentMethod>(request.Method?.Trim(), true, out var method)
            || method is not (PaymentMethod.Cash or PaymentMethod.Cheque or PaymentMethod.BankTransfer))
        {
            return ApiResults.ValidationProblem("The customer payment method is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Method)] = ["Use cash, cheque or bankTransfer."],
            });
        }

        var key = ReadIdempotencyKey(http);
        if (key is null)
        {
            return MissingIdempotencyKey();
        }

        var reference = request.Reference?.Trim();
        if (method is PaymentMethod.Cheque or PaymentMethod.BankTransfer
            && string.IsNullOrWhiteSpace(reference))
        {
            return ApiResults.ValidationProblem("The customer payment payload is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Reference)] = ["Cheque and bank transfer payments require a processing reference."],
            });
        }
        if (reference?.Length > 128)
        {
            return ApiResults.ValidationProblem("The customer payment payload is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Reference)] = ["References may contain at most 128 characters."],
            });
        }

        var allocations = request.Allocations ?? [];
        if (ValidateTargets([.. allocations], nameof(request.Allocations)) is { } targetError)
        {
            return targetError;
        }

        if (request.Amount <= 0 || Round(request.Amount) != request.Amount
            || allocations.Sum(item => item.Amount) > request.Amount)
        {
            return ApiResults.ValidationProblem("The customer payment amount is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Amount)] = ["Enter a positive amount with at most four decimal places that covers the listed allocations."],
            });
        }

        if (request.ReceivedDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            return ApiResults.ValidationProblem("The receipt date cannot be in the future.", new Dictionary<string, string[]>
            {
                [nameof(request.ReceivedDate)] = ["Enter today or an earlier receipt date."],
            });
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "customer.payment.create",
                key,
                new { customerId, request.Method, request.Amount, request.ReceivedDate, reference, allocations },
                async () =>
                {
                    var customer = await db.Customers
                        .FromSql($"SELECT * FROM customers WHERE id = {customerId} AND business_id = {businessId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer does not exist in this business.");

                    var business = await db.Businesses.AsNoTracking()
                        .SingleAsync(candidate => candidate.Id == businessId, cancellationToken)
                        .ConfigureAwait(false);

                    // Every target is locked and checked before a row is written: an issued invoice of this customer
                    // in this currency, or an opening receivable of the same party and currency, each with its own
                    // remaining balance. Openings are never patched - their remaining figure is derived from the
                    // applications against them, so the amount the import recorded stays as it was written.
                    var targets = await ResolveTargetsAsync(
                        db, businessId, [.. allocations], customer.Id, business.BaseCurrency, cancellationToken).ConfigureAwait(false);

                    var unallocated = LedgerWriter.Round(request.Amount - allocations.Sum(item => item.Amount));
                    var now = DateTimeOffset.UtcNow;
                    var payment = new Payment
                    {
                        BusinessId = businessId,
                        CustomerId = customer.Id,
                        Method = method,
                        Status = method == PaymentMethod.Cash ? PaymentStatus.Confirmed : PaymentStatus.Pending,
                        Amount = request.Amount,
                        UnallocatedAmount = unallocated,
                        Currency = business.BaseCurrency,
                        Reference = reference,
                        ReceivedAtUtc = now,
                        CreatedByUserId = userId,
                    };
                    db.Payments.Add(payment);

                    var allocationRows = allocations.Select(item => new PaymentAllocation
                    {
                        BusinessId = businessId,
                        PaymentId = payment.Id,
                        InvoiceId = item.InvoiceId,
                        OpenItemId = item.OpenItemId,
                        AllocatedAmount = item.Amount,
                        AllocatedAtUtc = now,
                    }).ToArray();
                    db.PaymentAllocations.AddRange(allocationRows);

                    if (method == PaymentMethod.Cash)
                    {
                        foreach (var (allocation, target) in allocations.Zip(targets))
                        {
                            if (target.Invoice is { } invoice)
                            {
                                ApplyInvoicePayment(invoice, allocation.Amount);
                            }
                        }

                        var accounts = await RequireAccountsAsync(db, businessId, ["1000", "1100", "2105"], cancellationToken).ConfigureAwait(false);
                        var journalLines = new List<JournalLineRequest>
                        {
                            new(accounts["1000"].Id, request.Amount, 0m, "Customer receipt", null),
                        };
                        journalLines.AddRange(targets.Select((target, index) =>
                            new JournalLineRequest(accounts["1100"].Id, 0m, allocations[index].Amount, $"Receipt allocated to {target.Reference}", target.LocationId)));
                        if (unallocated > 0)
                        {
                            journalLines.Add(new JournalLineRequest(accounts["2105"].Id, 0m, unallocated, "Unallocated customer deposit", null));
                        }

                        await LedgerWriter.PostWithinTransactionAsync(
                            db,
                            businessId,
                            userId,
                            business.BaseCurrency,
                            request.ReceivedDate,
                            $"Customer receipt {reference ?? payment.Id.ToString("N")}",
                            JournalSourceType.Payment,
                            payment.Id,
                            null,
                            journalLines,
                            cancellationToken).ConfigureAwait(false);
                    }

                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "payment",
                        EntityId = payment.Id,
                        Action = "customer_receipt_created",
                        OccurredAtUtc = now,
                        CorrelationId = http.TraceIdentifier,
                        Summary = System.Text.Json.JsonSerializer.Serialize(new { payment.Amount, payment.Method, payment.Status, payment.UnallocatedAmount, allocationCount = allocations.Count }),
                    });

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return (StatusCodes.Status201Created, await ToDtoAsync(db, payment, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> AllocateCustomerPaymentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        AllocateCustomerPaymentRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!TryGetScope(principal, out var businessId, out var userId))
        {
            return BusinessScope.MissingMembership();
        }
        var key = ReadIdempotencyKey(http);
        if (key is null)
        {
            return MissingIdempotencyKey();
        }
        var allocations = request.Allocations ?? [];
        if (allocations.Count == 0 || ValidateTargets([.. allocations], nameof(request.Allocations)) is { } targetError)
        {
            return ApiResults.ValidationProblem("At least one application naming exactly one document with a positive amount of at most four decimal places is required.", new Dictionary<string, string[]>
            {
                [nameof(request.Allocations)] = ["Name the invoice or the opening receivable this deposit settles, one amount each."],
            });
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "customer.payment.allocate",
                key,
                new { PaymentId = id, allocations },
                async () =>
                {
                    var payment = await db.Payments
                        .FromSql($"SELECT * FROM payments WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer payment does not exist in this business.");
                    if (payment.CustomerId is null || payment.SaleId is not null || payment.InvoiceId is not null
                        || payment.Status != PaymentStatus.Confirmed)
                    {
                        throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Only a confirmed customer receipt can be allocated from customer deposits.");
                    }

                    var allocationTotal = LedgerWriter.Round(allocations.Sum(item => item.Amount));
                    if (allocationTotal > payment.UnallocatedAmount)
                    {
                        throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The allocation exceeds the remaining customer deposit.");
                    }

                    var business = await db.Businesses.AsNoTracking()
                        .SingleAsync(candidate => candidate.Id == businessId, cancellationToken)
                        .ConfigureAwait(false);
                    var targets = await ResolveTargetsAsync(
                        db, businessId, [.. allocations], payment.CustomerId.Value, payment.Currency, cancellationToken).ConfigureAwait(false);

                    var accounts = await RequireAccountsAsync(db, businessId, ["1100", "2105"], cancellationToken).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow;
                    foreach (var (allocation, target) in allocations.Zip(targets))
                    {
                        if (target.Invoice is { } invoice)
                        {
                            ApplyInvoicePayment(invoice, allocation.Amount);
                        }

                        var row = new PaymentAllocation
                        {
                            BusinessId = businessId,
                            PaymentId = payment.Id,
                            InvoiceId = allocation.InvoiceId,
                            OpenItemId = allocation.OpenItemId,
                            AllocatedAmount = allocation.Amount,
                            AllocatedAtUtc = now,
                        };
                        db.PaymentAllocations.Add(row);
                        await LedgerWriter.PostWithinTransactionAsync(
                            db,
                            businessId,
                            userId,
                            payment.Currency,
                            LedgerWriter.LocalDate(business.TimeZone, now),
                            $"Apply customer deposit to {target.Reference}",
                            JournalSourceType.PaymentAllocation,
                            row.Id,
                            null,
                            [
                                new JournalLineRequest(accounts["2105"].Id, allocation.Amount, 0m, "Apply customer deposit", target.LocationId),
                                new JournalLineRequest(accounts["1100"].Id, 0m, allocation.Amount, $"Apply deposit to {target.Reference}", target.LocationId),
                            ],
                            cancellationToken).ConfigureAwait(false);
                    }

                    payment.UnallocatedAmount = LedgerWriter.Round(payment.UnallocatedAmount - allocationTotal);
                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "payment",
                        EntityId = payment.Id,
                        Action = "customer_deposit_allocated",
                        OccurredAtUtc = now,
                        CorrelationId = http.TraceIdentifier,
                        Summary = System.Text.Json.JsonSerializer.Serialize(new { allocationTotal, remainingDeposit = payment.UnallocatedAmount, invoiceCount = allocations.Count }),
                    });

                    return (StatusCodes.Status200OK, await ToDtoAsync(db, payment, cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// One settleable document a deposit application can name: an issued invoice, or an opening receivable the
    /// native import brought in. Both are locked before anything is written, and both are checked against the
    /// customer and currency of the receipt being applied - an opening for another party, or one held in another
    /// currency, is refused rather than quietly settled with someone else's money.
    /// </summary>
    private sealed record SettleTarget(
        Guid Id,
        bool IsInvoice,
        string Reference,
        Guid? LocationId,
        decimal RemainingAmount,
        Invoice? Invoice);

    /// <summary>
    /// Locks and validates every target of one command, in a stable order so two concurrent commands cannot each
    /// take what the other already committed. An invoice's available figure excludes tenders that are still
    /// pending against it; an opening's remaining figure is derived from the applications already made against it,
    /// because its imported original amount is immutable and is never patched.
    /// </summary>
    private static async Task<List<SettleTarget>> ResolveTargetsAsync(
        BillingDbContext db,
        Guid businessId,
        AllocationTarget[] allocations,
        Guid customerId,
        string currency,
        CancellationToken cancellationToken)
    {
        var invoiceIds = allocations.Where(item => item.InvoiceId is not null).Select(item => item.InvoiceId!.Value).Order().ToArray();
        var openItemIds = allocations.Where(item => item.OpenItemId is not null).Select(item => item.OpenItemId!.Value).Order().ToArray();

        var invoices = await LockInvoicesAsync(db, businessId, invoiceIds, cancellationToken).ConfigureAwait(false);
        if (invoices.Count != invoiceIds.Length)
        {
            throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "One or more invoices do not exist in this business.");
        }

        var openings = openItemIds.Length == 0
            ? []
            : await db.OpenItems
                .FromSql($"SELECT * FROM open_items WHERE business_id = {businessId} AND id = ANY({openItemIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        if (openings.Count != openItemIds.Length)
        {
            throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "One or more opening balances do not exist in this business.");
        }

        var byInvoice = invoices.ToDictionary(invoice => invoice.Id);
        var byOpening = openings.ToDictionary(item => item.Id);

        // What has already been applied to each opening is read once, in the same transaction that is about to
        // write the new applications, so a second command running concurrently sees the first one's rows.
        var appliedToOpenings = await db.PaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.BusinessId == businessId
                && allocation.OpenItemId != null
                && openItemIds.Contains(allocation.OpenItemId.Value))
            .GroupBy(allocation => allocation.OpenItemId!.Value)
            .Select(group => new { OpenItemId = group.Key, Applied = group.Sum(item => item.AllocatedAmount) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var appliedMap = appliedToOpenings.ToDictionary(row => row.OpenItemId, row => row.Applied);

        var targets = new List<SettleTarget>(allocations.Length);

        foreach (var allocation in allocations)
        {
            if (allocation.InvoiceId is { } invoiceId)
            {
                var invoice = byInvoice[invoiceId];
                if (invoice.CustomerId != customerId || invoice.Currency != currency
                    || invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                {
                    throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "An application must target an issued invoice for this customer in the receipt's currency.");
                }

                var pending = await PendingInvoiceAmountAsync(db, businessId, invoice.Id, cancellationToken).ConfigureAwait(false);
                var available = LedgerWriter.Round(invoice.BalanceDue - pending);
                if (allocation.Amount > available)
                {
                    throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, $"The requested application exceeds the {available} still owing on invoice {invoice.InvoiceNumber}.");
                }

                targets.Add(new SettleTarget(invoice.Id, true, invoice.InvoiceNumber, invoice.LocationId, available, invoice));
                continue;
            }

            var opening = byOpening[allocation.OpenItemId!.Value];
            if (opening.Kind != OpenItemKind.Receivable
                || opening.CustomerId != customerId
                || opening.Currency != currency)
            {
                throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Only an opening receivable of this customer, in the receipt's currency, can be settled from their deposit.");
            }

            var alreadyApplied = LedgerWriter.Round(appliedMap.GetValueOrDefault(opening.Id));
            var remaining = LedgerWriter.Round(opening.OriginalAmount - alreadyApplied);
            if (allocation.Amount > remaining)
            {
                throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, $"Only {remaining} is left of this opening balance; the amount the import recorded is never re-raised.");
            }

            targets.Add(new SettleTarget(opening.Id, false, opening.Reference ?? "opening balance", null, remaining, null));
        }

        return targets;
    }

    /// <summary>One posted event on a customer's account, before it is turned into a statement row.</summary>
    private sealed record StatementEvent(
        Guid Id,
        string Kind,
        Guid SettlesDocumentId,
        DateOnly DocumentDate,
        DateOnly? DueDate,
        string Reference,
        string? Note,
        string Currency,
        decimal DebitAmount,
        decimal CreditAmount);

    /// <summary>
    /// Gathers the account's events from the immutable rows that raised or moved the money, each dated by the
    /// business date it belongs to and included only when that date is at or before the statement date. An
    /// invoice's current balance is never trusted for a historical read: what settled it is the confirmed tenders,
    /// the deposit applications and the credit notes against it, and those are the rows collected here (task 25,
    /// recipe steps 1 and 5, T25.04).
    /// </summary>
    /// <param name="allowedLocationIds">The branches this membership may read, or null for every branch: a
    /// receivable raised at a branch the reader cannot act in is not theirs to see, exactly as the invoice list
    /// treats it. A customer deposit is not branch-bound, so the money held on the account stays visible.</param>
    private static async Task<(List<StatementEvent> Events, List<CustomerDepositDto> Deposits, decimal Unapplied)>
        BuildStatementAsync(
            BillingDbContext db,
            Guid businessId,
            Guid customerId,
            string currency,
            DateOnly asOf,
            IReadOnlyList<Guid>? allowedLocationIds,
            CancellationToken cancellationToken)
    {
        var invoices = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.BusinessId == businessId
                && invoice.CustomerId == customerId
                && invoice.Currency == currency
                && invoice.Status != InvoiceStatus.Draft
                && invoice.IssueDate <= asOf
                && (allowedLocationIds == null || invoice.LocationId == null || allowedLocationIds.Contains(invoice.LocationId.Value)))
            .Select(invoice => new { invoice.Id, invoice.InvoiceNumber, invoice.IssueDate, invoice.DueDate, invoice.TotalAmount, invoice.Note })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var openings = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId
                && item.Kind == OpenItemKind.Receivable
                && item.CustomerId == customerId
                && item.Currency == currency
                && item.DocumentDate <= asOf)
            .Select(item => new { item.Id, item.Reference, item.DocumentDate, item.DueDate, item.OriginalAmount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var invoiceIds = invoices.Select(invoice => invoice.Id).ToArray();

        // Tenders taken directly against an invoice, confirmed only: a cheque or transfer still awaiting clearance
        // has not become collected money and must not reduce what the customer owes.
        var directPayments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.InvoiceId != null
                && invoiceIds.Contains(payment.InvoiceId.Value)
                && payment.Status == PaymentStatus.Confirmed)
            .Select(payment => new { payment.Id, InvoiceId = payment.InvoiceId!.Value, payment.Amount, payment.ReceivedAtUtc, payment.Reference })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var applications = await db.PaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.BusinessId == businessId
                && allocation.InvoiceId != null
                && invoiceIds.Contains(allocation.InvoiceId.Value))
            .Join(db.Payments.AsNoTracking(), allocation => allocation.PaymentId, payment => payment.Id, (allocation, payment) => new { allocation, payment })
            .Where(row => row.payment.Status == PaymentStatus.Confirmed)
            .Select(row => new
            {
                row.allocation.Id,
                InvoiceId = row.allocation.InvoiceId!.Value,
                row.allocation.AllocatedAmount,
                row.allocation.AllocatedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var openingApplications = await db.PaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.BusinessId == businessId && allocation.OpenItemId != null)
            .Join(db.Payments.AsNoTracking(), allocation => allocation.PaymentId, payment => payment.Id, (allocation, payment) => new { allocation, payment })
            .Where(row => row.payment.CustomerId == customerId && row.payment.Status == PaymentStatus.Confirmed)
            .Select(row => new { row.allocation.Id, OpenItemId = row.allocation.OpenItemId!.Value, row.allocation.AllocatedAmount, row.allocation.AllocatedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var creditNotes = await (from note in db.CreditNotes.AsNoTracking()
                                 join invoice in db.Invoices.AsNoTracking() on new { Id = note.InvoiceId, note.BusinessId } equals new { Id = invoice.Id, invoice.BusinessId }
                                 where note.BusinessId == businessId && invoice.CustomerId == customerId
                                 select new { note.Id, note.CreditNumber, note.Amount, note.Reason, note.IssuedAtUtc, InvoiceId = invoice.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var events = new List<StatementEvent>();

        foreach (var invoice in invoices)
        {
            events.Add(new StatementEvent(
                invoice.Id,
                "invoice",
                invoice.Id,
                invoice.IssueDate,
                invoice.DueDate,
                string.IsNullOrEmpty(invoice.InvoiceNumber) ? "(unnumbered)" : invoice.InvoiceNumber,
                invoice.Note,
                currency,
                invoice.TotalAmount,
                0m));
        }

        foreach (var opening in openings)
        {
            events.Add(new StatementEvent(
                opening.Id,
                "opening",
                opening.Id,
                opening.DocumentDate,
                opening.DueDate,
                opening.Reference ?? "imported opening balance",
                null,
                currency,
                opening.OriginalAmount,
                0m));
        }

        foreach (var payment in directPayments)
        {
            var date = DateOnly.FromDateTime(payment.ReceivedAtUtc.UtcDateTime);
            if (date > asOf)
            {
                continue;
            }

            events.Add(new StatementEvent(
                payment.Id, "receipt", payment.InvoiceId, date, null,
                payment.Reference ?? "tender against the invoice", null, currency, 0m, payment.Amount));
        }

        foreach (var application in applications)
        {
            var date = DateOnly.FromDateTime(application.AllocatedAtUtc.UtcDateTime);
            if (date > asOf)
            {
                continue;
            }

            events.Add(new StatementEvent(
                application.Id, "application", application.InvoiceId, date, null,
                "deposit applied to the invoice", null, currency, 0m, application.AllocatedAmount));
        }

        foreach (var application in openingApplications)
        {
            var date = DateOnly.FromDateTime(application.AllocatedAtUtc.UtcDateTime);
            if (date > asOf)
            {
                continue;
            }

            events.Add(new StatementEvent(
                application.Id, "application", application.OpenItemId, date, null,
                "deposit applied to the opening balance", null, currency, 0m, application.AllocatedAmount));
        }

        foreach (var note in creditNotes)
        {
            var date = DateOnly.FromDateTime(note.IssuedAtUtc.UtcDateTime);
            if (date > asOf)
            {
                continue;
            }

            events.Add(new StatementEvent(
                note.Id, "credit_note", note.InvoiceId, date, null, note.CreditNumber, note.Reason, currency, 0m, note.Amount));
        }

        // What the customer has sitting on deposit, with the refunds already given back against it.
        var receipts = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.CustomerId == customerId
                && payment.SaleId == null
                && payment.InvoiceId == null
                && payment.Status == PaymentStatus.Confirmed)
            .OrderBy(payment => payment.ReceivedAtUtc)
            .Select(payment => new { payment.Id, payment.Method, payment.Status, payment.Amount, payment.UnallocatedAmount, payment.Currency, payment.Reference, payment.ReceivedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var refunds = await db.DepositRefunds.AsNoTracking()
            .Where(refund => refund.BusinessId == businessId)
            .Select(refund => new { refund.Id, refund.PaymentId, refund.Amount, refund.Currency, refund.Reason, refund.Method, refund.BankReference, refund.CashRegisterSessionId, refund.JournalEntryId, refund.OccurredAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var refundJournalNumbers = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && refunds.Select(refund => refund.JournalEntryId).Contains(entry.Id))
            .Select(entry => new { entry.Id, entry.EntryNumber })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var deposits = receipts
            .Where(receipt => DateOnly.FromDateTime(receipt.ReceivedAtUtc.UtcDateTime) <= asOf)
            .Select(receipt => new CustomerDepositDto(
                receipt.Id,
                receipt.Method.ToString(),
                receipt.Status.ToString(),
                receipt.Amount,
                receipt.UnallocatedAmount,
                receipt.Currency,
                receipt.Reference,
                receipt.ReceivedAtUtc,
                refunds
                    .Where(refund => refund.PaymentId == receipt.Id)
                    .OrderBy(refund => refund.OccurredAtUtc)
                    .Select(refund => new DepositRefundDto(
                        refund.Id,
                        refund.PaymentId,
                        refund.Amount,
                        refund.Currency,
                        refund.Reason,
                        refund.Method,
                        refund.BankReference,
                        refund.CashRegisterSessionId,
                        refundJournalNumbers.FirstOrDefault(entry => entry.Id == refund.JournalEntryId)?.EntryNumber,
                        refund.OccurredAtUtc,
                        LedgerWriter.Round(receipt.UnallocatedAmount)))
                    .ToList()))
            .ToList();

        return (events, deposits, LedgerWriter.Round(deposits.Sum(deposit => deposit.UnallocatedAmount)));
    }

    /// <summary>
    /// The customer's account as at one business date. Every row is a posted event, and the running balance is
    /// arithmetic over those events only, so a statement read at an earlier date never shows money that arrived
    /// after it (T25.04) and never shows a figure the ledger does not hold.
    /// </summary>
    private static async Task<IResult> CustomerStatementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid customerId,
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        if (!TryGetScope(principal, out var businessId, out _))
        {
            return BusinessScope.MissingMembership();
        }

        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == customerId, cancellationToken)
            .ConfigureAwait(false);
        if (customer is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer does not exist in this business.");
        }

        var business = await db.Businesses.AsNoTracking()
            .SingleAsync(row => row.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        // The reader's branch scope is resolved once and applied to every document the statement would name, so
        // a receivable belonging to another branch is not disclosed by an account balance (task 25, recipe step 1).
        if (!TryGetUserId(principal, out var statementUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var allowedLocations = await BusinessScope.AccessibleLocationIdsAsync(db, statementUserId, businessId, cancellationToken)
            .ConfigureAwait(false);
        var date = asOf ?? LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);

        // One clock, the business's own: a default computed in the business time zone must be checked in that same
        // time zone, or the route refuses its own answer during the hours when the zone is ahead of Greenwich
        // (specification F21; the same defect was found by the task 27 read and is fixed in both statements).
        var businessToday = LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);
        if (date > businessToday)
        {
            return ApiResults.ValidationProblem("The statement date cannot be in the future.", new Dictionary<string, string[]>
            {
                [nameof(asOf)] = [$"Choose {businessToday:yyyy-MM-dd} or an earlier business date."],
            });
        }

        var (events, deposits, unapplied) = await BuildStatementAsync(
            db, businessId, customerId, business.BaseCurrency, date, allowedLocations, cancellationToken).ConfigureAwait(false);

        // Ordered by the business date each event belongs to, then documents before settlements, then identity, so
        // two reads of the same account at the same date print in the same order.
        var ordered = events
            .OrderBy(row => row.DocumentDate)
            .ThenBy(row => row.DebitAmount > 0m ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToList();

        const int window = 400;
        var truncated = ordered.Count > window;
        var shown = truncated ? ordered[^window..] : ordered;
        var before = LedgerWriter.Round(ordered.Sum(row => row.DebitAmount - row.CreditAmount)
            - shown.Sum(row => row.DebitAmount - row.CreditAmount));

        var running = before;
        var lines = new List<CustomerStatementLineDto>(shown.Count);
        foreach (var row in shown)
        {
            running = LedgerWriter.Round(running + row.DebitAmount - row.CreditAmount);
            lines.Add(new CustomerStatementLineDto(
                row.Id, row.Kind, row.DocumentDate, row.DueDate, row.Reference, row.Note,
                row.DebitAmount, row.CreditAmount, running, row.Currency));
        }

        var closing = running;

        // The business reads its own money at its own settlement scale (task 24, finding F21), so the statement
        // carries the scale and zone it was composed in rather than leaving a screen to assume two decimals.
        var settlementScale = await db.BusinessAccountingSettings.AsNoTracking()
            .Where(settings => settings.BusinessId == businessId)
            .Select(settings => (int?)settings.SettlementScale)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new CustomerStatementDto(
            customerId,
            customer.Name,
            business.BaseCurrency,
            date,
            DateTimeOffset.UtcNow,
            before,
            LedgerWriter.Round(shown.Sum(row => row.DebitAmount)),
            LedgerWriter.Round(shown.Sum(row => row.CreditAmount)),
            LedgerWriter.Round(closing),
            unapplied,
            shown.Count(row => row.DebitAmount > 0m),
            truncated,
            lines,
            deposits,
            settlementScale,
            string.IsNullOrWhiteSpace(business.TimeZone) ? null : business.TimeZone));
    }

    /// <summary>
    /// The documents this customer has not fully settled: issued invoices and imported opening receivables side by
    /// side. Both are settleable from a deposit and both belong in the receivable control, so hiding an opening
    /// would leave the screen that clears it unable to see it (task 25, recipe step 2, T25.05).
    /// </summary>
    private static async Task<IResult> CustomerOpenItemsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid customerId,
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        if (!TryGetScope(principal, out var businessId, out _))
        {
            return BusinessScope.MissingMembership();
        }

        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == customerId, cancellationToken)
            .ConfigureAwait(false);
        if (customer is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer does not exist in this business.");
        }

        var business = await db.Businesses.AsNoTracking()
            .SingleAsync(row => row.Id == businessId, cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetUserId(principal, out var openItemUserId))
        {
            return BusinessScope.MissingMembership();
        }

        var allowedLocations = await BusinessScope.AccessibleLocationIdsAsync(db, openItemUserId, businessId, cancellationToken)
            .ConfigureAwait(false);
        var date = asOf ?? LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);

        var (events, _, _) = await BuildStatementAsync(
            db, businessId, customerId, business.BaseCurrency, date, allowedLocations, cancellationToken).ConfigureAwait(false);

        var rows = new List<CustomerOpenItemDto>();
        foreach (var raised in events.Where(row => row.DebitAmount > 0m).OrderBy(row => row.DocumentDate).ThenBy(row => row.Id))
        {
            var applied = events
                .Where(row => row.SettlesDocumentId == raised.Id && row.DebitAmount == 0m)
                .Sum(row => row.CreditAmount);
            var remaining = LedgerWriter.Round(raised.DebitAmount - applied);
            if (remaining <= 0m)
            {
                continue;
            }

            rows.Add(new CustomerOpenItemDto(
                raised.Kind,
                raised.Id,
                raised.Reference,
                raised.DocumentDate,
                raised.DueDate,
                raised.DebitAmount,
                LedgerWriter.Round(applied),
                remaining,
                raised.Currency,
                raised.DueDate is { } due && due > date,
                raised.Note));
        }

        return Results.Ok(rows);
    }

    /// <summary>
    /// Hands unapplied deposit money back to the customer (task 25, recipe step 4). The receipt row is locked first,
    /// so a refund and an application drawing on the same remaining deposit cannot both succeed: whoever arrives
    /// second reads the balance the first one left and is refused beyond it. Cash leaves through a drawer that
    /// actually holds it - the same D07 rule that guards a sale refund or a paid-out - and a bank settlement names
    /// the reference the bank will be matched by. The reversal journal, the reason, the amount and the operator are
    /// written once and never edited: a mistake is answered by a new linked event, not by rewriting the old one.
    /// </summary>
    private static async Task<IResult> RefundUnallocatedAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        RefundUnallocatedRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!TryGetScope(principal, out var businessId, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var key = ReadIdempotencyKey(http);
        if (key is null)
        {
            return MissingIdempotencyKey();
        }

        if (request is null || request.Amount <= 0 || Round(request.Amount) != request.Amount)
        {
            return ApiResults.ValidationProblem("The refund amount is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Amount)] = ["Enter a positive amount of at most four decimal places."],
            });
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 512)
        {
            return ApiResults.ValidationProblem("The refund payload is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = ["A refund reason of 1-512 characters is required; it is audited with who authorised it."],
            });
        }

        if (!Enum.TryParse<PaymentMethod>(request.Method?.Trim(), true, out var method)
            || method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            return ApiResults.ValidationProblem("The refund settlement method is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.Method)] = ["Refund the money as cash out of a drawer, or as a bank transfer with its settlement reference."],
            });
        }

        var bankReference = request.BankReference?.Trim();
        if (method == PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(bankReference))
        {
            return ApiResults.ValidationProblem("A bank refund needs its settlement reference.", new Dictionary<string, string[]>
            {
                [nameof(request.BankReference)] = ["Name the reference the bank movement will be matched by, so the money can be traced both ways."],
            });
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "customer.payment.refund_unallocated",
                key,
                new { PaymentId = id, request },
                async () =>
                {
                    var payment = await db.Payments
                        .FromSql($"SELECT * FROM payments WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer payment does not exist in this business.");

                    if (payment.CustomerId is null || payment.SaleId is not null || payment.InvoiceId is not null)
                    {
                        throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Only a receipt held on a customer's account can be refunded from their deposit.");
                    }

                    if (payment.Status != PaymentStatus.Confirmed)
                    {
                        throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Money that has not cleared cannot be handed back: it was never collected." );
                    }

                    if (request.Amount > payment.UnallocatedAmount)
                    {
                        throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, $"Only {payment.UnallocatedAmount} of this receipt is still unapplied; the rest has already settled a document.");
                    }

                    var business = await db.Businesses.AsNoTracking()
                        .SingleAsync(row => row.Id == businessId, cancellationToken)
                        .ConfigureAwait(false);

                    CashRegisterSession? session = null;
                    if (method == PaymentMethod.Cash)
                    {
                        if (request.RegisterId is not { } registerId)
                        {
                            throw Rule(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "A cash refund names the drawer the money comes out of.");
                        }

                        var register = await db.CashRegisters.AsNoTracking()
                            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == registerId && row.IsActive, cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw Rule(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "The register must be active and belong to this business.");

                        session = await db.CashRegisterSessions
                            .FromSql($"SELECT * FROM cash_register_sessions WHERE business_id = {businessId} AND cash_register_id = {registerId} AND status = {CashRegisterSessionStatus.Open} FOR UPDATE")
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.CashRegisterNotOpen, "The register has no open drawer session.");

                        await CashDrawerGuards.EnsureCashCoversAsync(
                            db, businessId, session, request.Amount, $"a deposit refund of {request.Amount}", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var accounts = await RequireAccountsAsync(
                        db, businessId, method == PaymentMethod.Cash ? ["1000", "2105"] : ["1010", "2105"], cancellationToken)
                        .ConfigureAwait(false);

                    var now = DateTimeOffset.UtcNow;

                    // The refund is its own event, so it carries its own identity into the ledger: the journal is
                    // keyed to the refund row rather than to the receipt, whose own posting already holds that
                    // source. A retried command therefore replays this refund instead of colliding with - or
                    // silently replacing - the receipt that first brought the money in.
                    var refund = new DepositRefund
                    {
                        BusinessId = businessId,
                        PaymentId = payment.Id,
                        Amount = request.Amount,
                        Currency = payment.Currency,
                        Reason = reason,
                        Method = method.ToString(),
                        CashRegisterSessionId = session?.Id,
                        BankReference = bankReference,
                        IdempotencyKey = key,
                        PayloadHash = RequestFingerprint.Compute(new { PaymentId = id, request }),
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };

                    var entry = await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        payment.Currency,
                        LedgerWriter.LocalDate(business.TimeZone, now),
                        $"Refund of unapplied customer deposit {payment.Reference ?? payment.Id.ToString("N")}",
                        JournalSourceType.Refund,
                        refund.Id,
                        reversalOfEntryId: null,
                        [
                            new JournalLineRequest(accounts["2105"].Id, request.Amount, 0m, "Deposit handed back", null),
                            new JournalLineRequest(
                                accounts[method == PaymentMethod.Cash ? "1000" : "1010"].Id,
                                0m,
                                request.Amount,
                                method == PaymentMethod.Cash ? "Cash out of the drawer" : $"Bank settlement {bankReference}",
                                null),
                        ],
                        cancellationToken).ConfigureAwait(false);

                    refund.JournalEntryId = entry.Id;
                    refund.OccurredAtUtc = now;
                    refund.RefundedByUserId = userId;
                    db.DepositRefunds.Add(refund);

                    if (session is not null)
                    {
                        db.CashRegisterMovements.Add(new CashRegisterMovement
                        {
                            BusinessId = businessId,
                            CashRegisterId = session.CashRegisterId,
                            CashRegisterSessionId = session.Id,
                            MovementType = CashMovementType.CashOut,
                            Amount = request.Amount,
                            Currency = payment.Currency,
                            Reason = $"Refund of unapplied deposit: {reason}",
                            CreatedByUserId = userId,
                            OccurredAtUtc = now,
                        });
                    }

                    // The receipt's remaining figure is the only place the deposit balance lives; the refund row is
                    // the immutable evidence of what left it, so a later read can prove the two still agree.
                    payment.UnallocatedAmount = LedgerWriter.Round(payment.UnallocatedAmount - request.Amount);

                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "payment",
                        EntityId = payment.Id,
                        Action = "customer_deposit_refunded",
                        OccurredAtUtc = now,
                        CorrelationId = http.TraceIdentifier,
                        Summary = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            refund.Amount,
                            refund.Method,
                            refund.BankReference,
                            reason,
                            remainingUnapplied = payment.UnallocatedAmount,
                            entry = entry.EntryNumber,
                        }),
                    });

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return (StatusCodes.Status201Created, new DepositRefundDto(
                        refund.Id,
                        refund.PaymentId,
                        refund.Amount,
                        refund.Currency,
                        refund.Reason,
                        refund.Method,
                        refund.BankReference,
                        refund.CashRegisterSessionId,
                        entry.EntryNumber,
                        refund.OccurredAtUtc,
                        payment.UnallocatedAmount));
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

    private static async Task<List<Invoice>> LockInvoicesAsync(
        BillingDbContext db,
        Guid businessId,
        Guid[] invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Length == 0)
        {
            return [];
        }

        return await db.Invoices
            .FromSql($"SELECT * FROM invoices WHERE business_id = {businessId} AND id = ANY({invoiceIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<decimal> PendingInvoiceAmountAsync(
        BillingDbContext db,
        Guid businessId,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        var direct = await db.Payments
            .Where(payment => payment.BusinessId == businessId && payment.InvoiceId == invoiceId && payment.Status == PaymentStatus.Pending)
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        var allocated = await (
            from allocation in db.PaymentAllocations
            join payment in db.Payments on new { allocation.PaymentId, allocation.BusinessId } equals new { PaymentId = payment.Id, payment.BusinessId }
            where allocation.BusinessId == businessId && allocation.InvoiceId == invoiceId && payment.Status == PaymentStatus.Pending
            select (decimal?)allocation.AllocatedAmount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        return LedgerWriter.Round(direct + allocated);
    }

    private static async Task<Dictionary<string, Account>> RequireAccountsAsync(
        BillingDbContext db,
        Guid businessId,
        string[] codes,
        CancellationToken cancellationToken)
    {
        var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, codes, cancellationToken).ConfigureAwait(false);
        foreach (var code in codes)
        {
            var expected = code == "2105" ? AccountType.Liability : AccountType.Asset;
            if (!accounts.TryGetValue(code, out var account) || !account.IsActive || account.AccountType != expected)
            {
                throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, $"The configured account for {code} is missing, inactive or has the wrong account type.");
            }
        }
        return accounts;
    }

    private static void ApplyInvoicePayment(Invoice invoice, decimal amount)
    {
        invoice.AmountPaid = LedgerWriter.Round(invoice.AmountPaid + amount);
        invoice.BalanceDue = LedgerWriter.Round(invoice.BalanceDue - amount);
        invoice.Status = invoice.BalanceDue == 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
    }

    private static async Task<CustomerPaymentDto> ToDtoAsync(
        BillingDbContext db,
        Payment payment,
        CancellationToken cancellationToken)
    {
        var allocations = await db.PaymentAllocations.AsNoTracking()
            .Where(item => item.BusinessId == payment.BusinessId && item.PaymentId == payment.Id)
            .OrderBy(item => item.AllocatedAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => new CustomerPaymentAllocationDto(item.InvoiceId, item.OpenItemId, item.AllocatedAmount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new CustomerPaymentDto(
            payment.Id,
            payment.CustomerId ?? Guid.Empty,
            payment.Method.ToString(),
            payment.Status.ToString(),
            payment.Amount,
            payment.UnallocatedAmount,
            payment.Currency,
            payment.Reference,
            payment.ReceivedAtUtc,
            allocations);
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static string? ReadIdempotencyKey(HttpContext http)
    {
        var key = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        return key.Length is > 0 and <= 128 ? key : null;
    }

    private static IResult MissingIdempotencyKey()
        => ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]>
        {
            [IdempotencyHeader] = ["Send a client-generated key of 1-128 characters."],
        });

    private static bool TryGetScope(ClaimsPrincipal principal, out Guid businessId, out Guid userId)
    {
        businessId = Guid.Empty;
        userId = Guid.Empty;
        return principal.TryGetBusinessId(out businessId)
            && Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
    }

    private static DomainRuleException Rule(int statusCode, string code, string message)
        => new(statusCode, code, message, message);

    private static decimal Round(decimal amount)
        => decimal.Round(amount, MoneyScale, MidpointRounding.AwayFromZero);

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This payment command conflicts with an existing record.", "Refresh the payment and retry with a new idempotency key if the request changed."),
            PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The payment violates a current business rule.", "Refresh the invoice and customer balances before retrying."),
            _ => ApiResults.Problem(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, "The payment could not be completed."),
        };
}
