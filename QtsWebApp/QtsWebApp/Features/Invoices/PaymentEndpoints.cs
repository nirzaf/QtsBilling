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
        return app;
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
        if (allocations.Count > 100
            || allocations.Any(item => item.InvoiceId == Guid.Empty || item.Amount <= 0 || Round(item.Amount) != item.Amount)
            || allocations.Select(item => item.InvoiceId).Distinct().Count() != allocations.Count)
        {
            return ApiResults.ValidationProblem("Invoice allocations must be unique, positive amounts with at most four decimal places.", new Dictionary<string, string[]>
            {
                [nameof(request.Allocations)] = ["Remove duplicate invoices and enter a positive amount for each invoice."],
            });
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
                    var invoiceIds = allocations.Select(item => item.InvoiceId).Order().ToArray();
                    var invoices = await LockInvoicesAsync(db, businessId, invoiceIds, cancellationToken).ConfigureAwait(false);
                    if (invoices.Count != invoiceIds.Length)
                    {
                        throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "One or more invoices do not exist in this business.");
                    }

                    var byId = invoices.ToDictionary(invoice => invoice.Id);
                    foreach (var allocation in allocations)
                    {
                        var invoice = byId[allocation.InvoiceId];
                        if (invoice.CustomerId != customer.Id || invoice.Currency != business.BaseCurrency
                            || invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                        {
                            throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "An allocation must target an issued invoice for this customer in the business currency.");
                        }

                        var pending = await PendingInvoiceAmountAsync(db, businessId, invoice.Id, cancellationToken).ConfigureAwait(false);
                        if (allocation.Amount > LedgerWriter.Round(invoice.BalanceDue - pending))
                        {
                            throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, $"The requested allocation exceeds the available balance for invoice {invoice.InvoiceNumber}.");
                        }
                    }

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
                        AllocatedAmount = item.Amount,
                        AllocatedAtUtc = now,
                    }).ToArray();
                    db.PaymentAllocations.AddRange(allocationRows);

                    if (method == PaymentMethod.Cash)
                    {
                        foreach (var item in allocations)
                        {
                            ApplyInvoicePayment(byId[item.InvoiceId], item.Amount);
                        }

                        var accounts = await RequireAccountsAsync(db, businessId, ["1000", "1100", "2105"], cancellationToken).ConfigureAwait(false);
                        var journalLines = new List<JournalLineRequest>
                        {
                            new(accounts["1000"].Id, request.Amount, 0m, "Customer receipt", null),
                        };
                        journalLines.AddRange(allocations.Select(item =>
                            new JournalLineRequest(accounts["1100"].Id, 0m, item.Amount, $"Receipt allocated to {byId[item.InvoiceId].InvoiceNumber}", byId[item.InvoiceId].LocationId)));
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
        if (allocations.Count is 0 or > 100
            || allocations.Any(item => item.InvoiceId == Guid.Empty || item.Amount <= 0 || Round(item.Amount) != item.Amount)
            || allocations.Select(item => item.InvoiceId).Distinct().Count() != allocations.Count)
        {
            return ApiResults.ValidationProblem("At least one unique, positive invoice allocation with at most four decimal places is required.", new Dictionary<string, string[]>
            {
                [nameof(request.Allocations)] = ["Remove duplicate invoices and enter a positive amount for each invoice."],
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

                    var invoiceIds = allocations.Select(item => item.InvoiceId).Order().ToArray();
                    var invoices = await LockInvoicesAsync(db, businessId, invoiceIds, cancellationToken).ConfigureAwait(false);
                    if (invoices.Count != invoiceIds.Length)
                    {
                        throw Rule(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "One or more invoices do not exist in this business.");
                    }
                    var byId = invoices.ToDictionary(invoice => invoice.Id);
                    foreach (var allocation in allocations)
                    {
                        var invoice = byId[allocation.InvoiceId];
                        if (invoice.CustomerId != payment.CustomerId || invoice.Currency != payment.Currency
                            || invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                        {
                            throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "A deposit can only be allocated to an issued invoice for the same customer and currency.");
                        }
                        var pending = await PendingInvoiceAmountAsync(db, businessId, invoice.Id, cancellationToken).ConfigureAwait(false);
                        if (allocation.Amount > LedgerWriter.Round(invoice.BalanceDue - pending))
                        {
                            throw Rule(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, $"The requested allocation exceeds the available balance for invoice {invoice.InvoiceNumber}.");
                        }
                    }

                    var accounts = await RequireAccountsAsync(db, businessId, ["1100", "2105"], cancellationToken).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow;
                    foreach (var item in allocations)
                    {
                        var invoice = byId[item.InvoiceId];
                        ApplyInvoicePayment(invoice, item.Amount);
                        var row = new PaymentAllocation
                        {
                            BusinessId = businessId,
                            PaymentId = payment.Id,
                            InvoiceId = invoice.Id,
                            AllocatedAmount = item.Amount,
                            AllocatedAtUtc = now,
                        };
                        db.PaymentAllocations.Add(row);
                        await LedgerWriter.PostWithinTransactionAsync(
                            db,
                            businessId,
                            userId,
                            payment.Currency,
                            LedgerWriter.LocalDate(null, now),
                            $"Apply customer deposit to {invoice.InvoiceNumber}",
                            JournalSourceType.PaymentAllocation,
                            row.Id,
                            null,
                            [
                                new JournalLineRequest(accounts["2105"].Id, item.Amount, 0m, "Apply customer deposit", invoice.LocationId),
                                new JournalLineRequest(accounts["1100"].Id, 0m, item.Amount, $"Apply deposit to {invoice.InvoiceNumber}", invoice.LocationId),
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
            .Select(item => new CustomerPaymentAllocationDto(item.InvoiceId, item.AllocatedAmount))
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
