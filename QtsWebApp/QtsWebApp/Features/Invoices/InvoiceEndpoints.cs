using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
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

/// <summary>
/// Immutable invoice and credit workflow (QB-11). Drafts are editable; issuing allocates the number inside
/// the posting transaction (row-locked sequence + partial unique index), snapshots every line and posts the
/// receivable entry. Posted financial terms are never edited: corrections are void (no payments) or a credit
/// note that references the original invoice and reverses its entry (F-10, spec §3.7).
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

    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invoices").WithTags("Invoices");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapPost("/{id:guid}/issue", IssueAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPost("/{id:guid}/void", VoidAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapPost("/{id:guid}/payments", AddPaymentAsync).RequireAuthorization(Permissions.InvoicesIssue);
        group.MapPost("/{id:guid}/credit-notes", CreditAsync).RequireAuthorization(Permissions.InvoicesManage);
        group.MapGet("/{id:guid}/print", PrintAsync).RequireAuthorization(Permissions.InvoicesIssue);

        return app;
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

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.Invoices.AsNoTracking().Where(invoice => invoice.BusinessId == businessId);

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
                invoice.Note,
                invoice.IssuedAtUtc,
                invoice.VoidedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var ids = rows.Select(row => row.Id).ToArray();

        var paid = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.InvoiceId != null
                && ids.Contains(payment.InvoiceId!.Value)
                && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
            .GroupBy(payment => payment.InvoiceId!.Value)
            .Select(group => new { InvoiceId = group.Key, Amount = group.Sum(payment => payment.Amount) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var credited = await db.CreditNotes.AsNoTracking()
            .Where(note => note.BusinessId == businessId && ids.Contains(note.InvoiceId))
            .GroupBy(note => note.InvoiceId)
            .Select(group => new { InvoiceId = group.Key, Amount = group.Sum(note => note.Amount) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var paidByInvoice = paid.ToDictionary(row => row.InvoiceId, row => row.Amount);
        var creditedByInvoice = credited.ToDictionary(row => row.InvoiceId, row => row.Amount);

        var customerIds = rows.Where(row => row.CustomerId is not null).Select(row => row.CustomerId!.Value).Distinct().ToArray();

        var customers = await db.Customers.AsNoTracking()
            .Where(candidate => candidate.BusinessId == businessId && customerIds.Contains(candidate.Id))
            .Select(candidate => new { candidate.Id, candidate.Name })
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.Name, cancellationToken)
            .ConfigureAwait(false);

        var page = rows
            .Select(row => ToSummary(
                row,
                row.CustomerId is { } id && customers.TryGetValue(id, out var name) ? name : null,
                paidByInvoice.TryGetValue(row.Id, out var paidAmount) ? paidAmount : 0m,
                creditedByInvoice.TryGetValue(row.Id, out var creditedAmount) ? creditedAmount : 0m))
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

        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null)
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

        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null)
        {
            return NotFoundInvoice();
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        Customer? customer = null;

        if (invoice.CustomerId is { } customerId)
        {
            customer = await db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken)
                .ConfigureAwait(false);
        }

        var detail = await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false);

        return Results.Content(InvoicePrintRenderer.RenderHtml(business, customer, detail), "text/html", System.Text.Encoding.UTF8);
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

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var prepared = await PrepareAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var lines = prepared.Lines!;
        var totals = TotalsOf(lines);

        var invoice = new Invoice
        {
            BusinessId = businessId,
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
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        };

        db.Invoices.Add(invoice);
        AddLines(db, businessId, invoice.Id, lines);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/invoices/{invoice.Id}",
            await ToDetailAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false));
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

        var invoice = await db.Invoices
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null)
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

        var prepared = await PrepareAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

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

            // Draft lines are replaceable; the immutable snapshot is the issued invoice (D-38).
            await db.InvoiceLines
                .Where(line => line.BusinessId == businessId && line.InvoiceId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            var target = await db.Invoices
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            target.LocationId = request.LocationId;
            target.CustomerId = request.CustomerId;
            target.IssueDate = prepared.IssueDate;
            target.DueDate = request.DueDate;
            target.SubtotalAmount = totals.Subtotal;
            target.DiscountAmount = totals.Discount;
            target.TaxAmount = totals.Tax;
            target.TotalAmount = totals.Total;
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

        var invoice = await db.Invoices
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null)
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

                if (invoice is null)
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

                var lines = await db.InvoiceLines.AsNoTracking()
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

                var accounts = await LedgerWriter.ResolveAccountsAsync(
                        db,
                        businessId,
                        [ReceivableAccountCode, RevenueAccountCode, TaxAccountCode],
                        cancellationToken)
                    .ConfigureAwait(false);

                RejectMissingAccounts(accounts, ReceivableAccountCode, RevenueAccountCode, TaxAccountCode);

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
                    new(accounts[RevenueAccountCode].Id, 0m, LedgerWriter.Round(invoice.SubtotalAmount - invoice.DiscountAmount), $"Invoice {number}", invoice.LocationId),
                };

                if (invoice.TaxAmount > 0)
                {
                    journalLines.Add(new JournalLineRequest(accounts[TaxAccountCode].Id, 0m, invoice.TaxAmount, $"Invoice {number} tax", invoice.LocationId));
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

        var reason = request?.Reason?.Trim() ?? string.Empty;

        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem(
                "The void payload is invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"A void reason of 1-{MaxReasonLength} characters is required (audited with the actor and time)."] });
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

                if (invoice.Status == InvoiceStatus.Void)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "This invoice is already void.",
                        "Voiding is a one-time transition; the original entries remain in the ledger.");
                }

                var paid = await db.Payments.AsNoTracking()
                    .Where(payment => payment.BusinessId == businessId
                        && payment.InvoiceId == id
                        && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
                    .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
                    .ConfigureAwait(false) ?? 0m;

                if (paid > 0)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceHasPayments,
                        "An invoice with confirmed payments cannot be voided.",
                        "Record the return as a credit note referencing this invoice instead.");
                }

                var entry = await ReverseInvoiceEntryAsync(
                        db, businessId, userId, invoice, $"Void of {invoice.InvoiceNumber}", cancellationToken)
                    .ConfigureAwait(false);

                invoice.Status = InvoiceStatus.Void;
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
                        note = request.Note,
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
            var creditNote = await strategy.ExecuteAsync(async () =>
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

                var existing = await db.CreditNotes.AsNoTracking()
                    .FirstOrDefaultAsync(note => note.BusinessId == businessId && note.InvoiceId == id, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceAlreadyCredited,
                        "This invoice already has a credit note.",
                        "A full credit note can be issued at most once per invoice (partial credits are not implemented).");
                }

                if (invoice.Status == InvoiceStatus.Draft)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvoiceNotIssued,
                        "Only issued invoices can be credited.",
                        "Draft invoices are simply deleted.");
                }

                if (invoice.Status == InvoiceStatus.Void)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "This invoice is void; there is nothing left to credit.",
                        "Void or credited invoices cannot be credited again.");
                }

                var entry = await ReverseInvoiceEntryAsync(
                        db, businessId, userId, invoice, $"Credit note for {invoice.InvoiceNumber}", cancellationToken)
                    .ConfigureAwait(false);

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
                    Amount = invoice.TotalAmount,
                    Reason = reason,
                    Note = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim(),
                    JournalEntryId = entry.Id,
                    IssuedAtUtc = now,
                    IssuedByUserId = userId,
                };

                db.CreditNotes.Add(creditNote);

                invoice.Status = InvoiceStatus.Void;
                invoice.VoidedAtUtc = now;

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
                        amount = invoice.TotalAmount,
                        reversalEntry = entry.EntryNumber,
                        reason,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return creditNote;
            }).ConfigureAwait(false);

            var refreshed = await db.Invoices.AsNoTracking()
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/invoices/{id}/credit-notes/{creditNote.Id}",
                await ToDetailAsync(db, businessId, refreshed, cancellationToken).ConfigureAwait(false));
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
                new Dictionary<string, string[]> { ["method"] = ["Use 'cash', 'cheque' or 'bankTransfer' for invoice payments."] });
        }

        if (method is PaymentMethod.Card)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PaymentMethodDisabled,
                "Card payments are disabled.",
                "No verified PSP integration exists (OPEN-6); accept cash, cheque or bank transfer.");
        }

        var amount = LedgerWriter.Round(request.Amount);

        if (request.Amount <= 0 || amount != request.Amount)
        {
            return ApiResults.ValidationProblem(
                "The payment payload is invalid.",
                new Dictionary<string, string[]> { ["amount"] = [$"A positive amount of at most {MoneyScale} decimal places is required."] });
        }

        var reference = request.Reference?.Trim();

        if (method is PaymentMethod.Cheque or PaymentMethod.BankTransfer && reference is null or { Length: 0 })
        {
            return ApiResults.ValidationProblem(
                "The payment payload is invalid.",
                new Dictionary<string, string[]> { ["reference"] = ["Cheque and bank transfer payments require a processing reference."] });
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

                var paid = await db.Payments
                    .Where(candidate => candidate.BusinessId == businessId
                        && candidate.InvoiceId == id
                        && candidate.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
                    .SumAsync(candidate => (decimal?)candidate.Amount, cancellationToken)
                    .ConfigureAwait(false) ?? 0m;

                var remaining = LedgerWriter.Round(lockedInvoice.TotalAmount - paid);

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
                    Status = QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed,
                    Reference = reference,
                    ReceivedAtUtc = now,
                    CreatedByUserId = userId,
                    CashRegisterSessionId = lockedSession?.Id,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                db.Payments.Add(payment);

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

                var totalPaid = LedgerWriter.Round(paid + amount);
                lockedInvoice.Status = totalPaid >= lockedInvoice.TotalAmount ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;

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
                        totalPaid,
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

                if (line.VariantId is { } variantId)
                {
                    var variant = await db.ProductVariants.AsNoTracking()
                        .Where(candidate => candidate.BusinessId == businessId && candidate.Id == variantId)
                        .Select(candidate => new { candidate.Id, candidate.ProductId, candidate.Sku, candidate.IsActive })
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
                            .Select(candidate => new { candidate.Name, candidate.IsActive })
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
                        }
                    }
                }
                else if (name is null or { Length: 0 } || name.Length > MaxNameLength)
                {
                    errors[$"{key}.productName"] = [$"A line without a variant requires a product name of 1-{MaxNameLength} characters."];
                }

                var net = LedgerWriter.Round(gross - Math.Max(0, line.DiscountAmount));
                var tax = LedgerWriter.Round(net * line.TaxRate);

                lines.Add(new PricedLine(
                    line.VariantId,
                    name ?? string.Empty,
                    sku,
                    line.Quantity,
                    line.UnitPrice,
                    Math.Max(0, line.DiscountAmount),
                    line.TaxRate,
                    tax,
                    LedgerWriter.Round(net + tax)));

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
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                LineTotal = line.LineTotal,
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

    private static async Task<InvoiceDetailDto> ToDetailAsync(
        BillingDbContext db,
        Guid businessId,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.InvoiceId == invoice.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new InvoiceLineDto(
                line.LineNo,
                line.VariantId,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.TaxRate,
                line.TaxAmount,
                line.LineTotal))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId && payment.InvoiceId == invoice.Id)
            .OrderBy(payment => payment.ReceivedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var creditNote = await db.CreditNotes.AsNoTracking()
            .FirstOrDefaultAsync(note => note.BusinessId == businessId && note.InvoiceId == invoice.Id, cancellationToken)
            .ConfigureAwait(false);

        InvoiceCreditNoteDto? creditDto = null;

        if (creditNote is not null)
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

            creditDto = new InvoiceCreditNoteDto(
                creditNote.Id,
                creditNote.CreditNumber,
                creditNote.Amount,
                creditNote.Reason,
                creditNote.Note,
                creditNote.IssuedAtUtc,
                journalNumber);
        }

        return new InvoiceDetailDto(
            await ToSummaryAsync(db, businessId, invoice, cancellationToken).ConfigureAwait(false),
            lines,
            payments.Select(ToPaymentDto).ToList(),
            creditDto);
    }

    private static async Task<InvoiceSummaryDto> ToSummaryAsync(
        BillingDbContext db,
        Guid businessId,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        var paid = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.InvoiceId == invoice.Id
                && payment.Status == QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed)
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        var credited = await db.CreditNotes.AsNoTracking()
            .Where(note => note.BusinessId == businessId && note.InvoiceId == invoice.Id)
            .SumAsync(note => (decimal?)note.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        string? customerName = null;

        if (invoice.CustomerId is { } customerId)
        {
            customerName = await db.Customers.AsNoTracking()
                .Where(candidate => candidate.Id == customerId)
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
                invoice.Note,
                invoice.IssuedAtUtc,
                invoice.VoidedAtUtc),
            customerName,
            paid,
            credited);
    }

    private static InvoiceSummaryDto ToSummary(InvoiceRow row, string? customerName, decimal paid, decimal credited)
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
            paid,
            credited,
            row.Note,
            row.IssuedAtUtc,
            row.VoidedAtUtc);

    private static InvoicePaymentDto ToPaymentDto(Payment payment)
        => new(
            payment.Id,
            payment.Method.ToString(),
            EnumBridge.To<QtsBilling.Contracts.Sales.PaymentStatus>(payment.Status).ToString(),
            payment.Amount,
            payment.Currency,
            payment.Reference,
            payment.ReceivedAtUtc);

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
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_credit_notes_business_invoice" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.InvoiceAlreadyCredited,
                    "This invoice already has a credit note."),
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_payments_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same key committed first.",
                    "Retry the same payload: the stored payment will be returned instead of charging twice."),
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
        decimal LineTotal);

    private sealed record InvoiceTotals(decimal Subtotal, decimal Discount, decimal Tax, decimal Total);

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
        string? Note,
        DateTimeOffset? IssuedAtUtc,
        DateTimeOffset? VoidedAtUtc);

    private sealed record RegisterSession(Guid RegisterId, Guid SessionId);

    private sealed record RegisterContext(IResult? Error, RegisterSession? Session);
}
