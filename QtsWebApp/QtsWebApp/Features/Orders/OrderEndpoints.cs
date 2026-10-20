using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Orders;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsModels.ValueObjects;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Orders;

/// <summary>
/// Native quote and sales-order lifecycle. These documents capture commercial commitments only;
/// posting revenue and moving stock remain owned by invoice and fulfillment workflows.
/// </summary>
public static class OrderEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxLines = 100;
    private const int MaxNoteLength = 2_000;
    private const int MaxReasonLength = 500;

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var quotes = app.MapGroup("/api/v1/quotes")
            .WithTags("Orders")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage);
        quotes.MapGet("/", ListQuotesAsync);
        quotes.MapPost("/", CreateQuoteAsync);
        quotes.MapGet("/{id:guid}", GetQuoteAsync);
        quotes.MapPut("/{id:guid}", UpdateQuoteAsync);
        quotes.MapPost("/{id:guid}/send", SendQuoteAsync);
        quotes.MapPost("/{id:guid}/accept", AcceptQuoteAsync);
        quotes.MapPost("/{id:guid}/reject", RejectQuoteAsync);
        quotes.MapPost("/{id:guid}/amend", AmendQuoteAsync);
        quotes.MapPost("/{id:guid}/cancel", CancelQuoteAsync);
        quotes.MapPost("/{id:guid}/convert", ConvertQuoteAsync);

        var orders = app.MapGroup("/api/v1/sales-orders")
            .WithTags("Orders")
            .RequireAuthorization(Permissions.SalesPost, Permissions.OrdersManage);
        orders.MapGet("/", ListOrdersAsync);
        orders.MapPost("/", CreateOrderAsync);
        orders.MapGet("/{id:guid}", GetOrderAsync);
        orders.MapPut("/{id:guid}", UpdateOrderAsync);
        orders.MapPost("/{id:guid}/confirm", ConfirmOrderAsync);
        orders.MapPost("/{id:guid}/amend", AmendOrderAsync);
        orders.MapPost("/{id:guid}/cancel-remainder", CancelRemainderAsync);
        return app;
    }

    private static async Task<IResult> ListQuotesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? status,
        string? search,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var access = await GetLocationAccessAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
        if (access is null) return BusinessScope.MissingMembership();
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.SalesQuotes.AsNoTracking().Where(row => row.BusinessId == businessId);
        if (!access.AllLocations) query = query.Where(row => db.BusinessMembershipLocations.Any(grant => grant.BusinessId == businessId && grant.MembershipId == access.MembershipId && grant.LocationId == row.LocationId));
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SalesQuoteStatus>(status.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
                return ApiResults.ValidationProblem("The quote filter is invalid.", new Dictionary<string, string[]> { ["status"] = ["Choose a supported quote status."] });
            query = query.Where(row => row.Status == parsed);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(row => EF.Functions.ILike(row.QuoteNumber, pattern));
        }
        if (CursorPaging.TryDecode(cursor, out var cursorId)) query = query.Where(row => row.Id.CompareTo(cursorId) < 0);
        var rows = await query.OrderByDescending(row => row.Id).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        var items = rows.Select(ToQuoteSummary).ToArray();
        return Results.Ok(new PagedResult<SalesQuoteSummaryDto>(items, hasMore ? CursorPaging.Encode(rows[^1].Id) : null, size));
    }

    private static async Task<IResult> GetQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var quote = await db.SalesQuotes.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (quote is null) return NotFoundQuote();
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
        return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateQuoteAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CreateQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
        if (!ValidIdempotencyKey(http.Request.Headers[IdempotencyHeader].ToString(), out var key)) return MissingIdempotencyKey();
        if (!ValidNote(request.Note)) return InvalidNote();
        var dateResult = await GetDocumentCalendarAsync(db, businessId, request.LocationId, cancellationToken).ConfigureAwait(false);
        if (dateResult.Error is not null) return dateResult.Error;
        var quoteDate = request.QuoteDate ?? dateResult.Today;
        if (request.ExpiryDate < quoteDate || (request.PromisedDeliveryDate is { } promised && promised < quoteDate)) return InvalidDates();
        if (request.Lines is null) return InvalidLines("Add at least one line to the quote.");
        var business = await GetBusinessAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        if (business is null) return BusinessScope.MissingMembership();
        var linesResult = await PriceLinesAsync(db, businessId, ToLineInputs(request.Lines), await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (linesResult.Error is not null) return linesResult.Error;
        var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
        if (allowance is null) return BusinessScope.MissingMembership();
        var discountError = CheckDiscountAllowance(linesResult.Lines, allowance.Value);
        if (discountError is not null) return discountError;
        if (request.CustomerId is { } customerId && !await CustomerExistsAsync(db, businessId, customerId, cancellationToken).ConfigureAwait(false)) return NotFoundCustomer();
        var payloadHash = RequestFingerprint.Compute(request);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockIdempotencyKeyAsync(db, businessId, "quote-create", key, cancellationToken).ConfigureAwait(false);
            var existing = await db.SalesQuotes.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)) return IdempotencyConflict();
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(await QuoteDetailAsync(db, existing, cancellationToken).ConfigureAwait(false));
            }
    
            var number = await AllocateDocumentNumberAsync(db, businessId, request.LocationId, "quote", "QT-", quoteDate.Year, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var quote = new SalesQuote
            {
                BusinessId = businessId,
                LocationId = request.LocationId,
                CustomerId = request.CustomerId,
                QuoteNumber = number,
                Status = SalesQuoteStatus.Draft,
                QuoteDate = quoteDate,
                ExpiryDate = request.ExpiryDate,
                PromisedDeliveryDate = request.PromisedDeliveryDate,
                Currency = business.BaseCurrency,
                SubtotalAmount = linesResult.Document!.GrossAmount,
                DiscountAmount = linesResult.Document.DiscountAmount,
                TaxAmount = linesResult.Document.TaxAmount,
                TotalAmount = linesResult.Document.SettlementPayableAmount,
                RoundingAmount = linesResult.Document.RoundingAmount,
                CreatedByUserId = userId,
                Note = NormalizeNote(request.Note),
                IdempotencyKey = key,
                PayloadHash = payloadHash,
            };
            db.SalesQuotes.Add(quote);
            db.SalesQuoteLines.AddRange(linesResult.Lines.Select(line => ToQuoteLine(businessId, quote.Id, line)));
            AddAudit(db, businessId, userId, "sales_quote", quote.Id, "created", now, http.TraceIdentifier, new { quote.QuoteNumber, quote.TotalAmount });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v1/quotes/{quote.Id}", await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> UpdateQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UpdateQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (quote.Status != SalesQuoteStatus.Draft)
            {
                if (quote.CurrentRevision > 0 && quote.Status is SalesQuoteStatus.Sent or SalesQuoteStatus.Accepted) return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
                return QuoteStateConflict();
            }
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            if (!ValidNote(request.Note)) return InvalidNote();
            var dates = await GetDocumentCalendarAsync(db, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false);
            if (dates.Error is not null) return dates.Error;
            if (request.ExpiryDate < quote.QuoteDate || (request.PromisedDeliveryDate is { } promised && promised < quote.QuoteDate)) return InvalidDates();
            if (request.Lines is null) return InvalidLines("Add at least one line to the quote.");
            if (request.CustomerId is { } customerId && !await CustomerExistsAsync(db, businessId, customerId, cancellationToken).ConfigureAwait(false)) return NotFoundCustomer();
            var pricing = await PriceLinesAsync(db, businessId, ToLineInputs(request.Lines), await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (pricing.Error is not null) return pricing.Error;
            var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
            if (allowance is null) return BusinessScope.MissingMembership();
            var discountError = CheckDiscountAllowance(pricing.Lines, allowance.Value);
            if (discountError is not null) return discountError;
    
            quote.CustomerId = request.CustomerId;
            quote.ExpiryDate = request.ExpiryDate;
            quote.PromisedDeliveryDate = request.PromisedDeliveryDate;
            quote.Note = NormalizeNote(request.Note);
            ApplyDocumentAmounts(quote, pricing.Document!);
            var oldLines = await db.SalesQuoteLines.Where(line => line.BusinessId == businessId && line.QuoteId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            db.SalesQuoteLines.RemoveRange(oldLines);
            db.SalesQuoteLines.AddRange(pricing.Lines.Select(line => ToQuoteLine(businessId, id, line)));
            AddAudit(db, businessId, userId, "sales_quote", id, "draft_updated", DateTimeOffset.UtcNow, http.TraceIdentifier, new { request.ExpectedVersion });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> SendQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        QuoteCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (quote.Status != SalesQuoteStatus.Draft) return QuoteStateConflict();
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            var lines = await db.SalesQuoteLines.AsNoTracking().Where(line => line.BusinessId == businessId && line.QuoteId == id).OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (lines.Count == 0) return InvalidLines("A quote needs at least one line before it can be sent.");
            var date = await GetDocumentCalendarAsync(db, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false);
            if (date.Error is not null) return date.Error;
            if (quote.ExpiryDate < date.Today)
            {
                quote.Status = SalesQuoteStatus.Expired;
                AddAudit(db, businessId, userId, "sales_quote", id, "expired", DateTimeOffset.UtcNow, http.TraceIdentifier, new { quote.CurrentRevision, quote.ExpiryDate });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return QuoteExpiredResult();
            }
            var actorAllowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
            if (actorAllowance is null) return BusinessScope.MissingMembership();
            var discountError = CheckDiscountAllowance(lines.Select(ToPricedLine).ToArray(), actorAllowance.Value);
            if (discountError is not null) return discountError;
            var revision = NewQuoteRevision(businessId, quote, lines, 1, userId, string.IsNullOrWhiteSpace(request.Reason) ? "Sent to customer" : request.Reason.Trim());
            db.SalesQuoteRevisions.Add(revision.Header);
            db.SalesQuoteRevisionLines.AddRange(revision.Lines);
            quote.CurrentRevision = 1;
            quote.Status = SalesQuoteStatus.Sent;
            quote.SentByUserId = userId;
            quote.SentAtUtc = DateTimeOffset.UtcNow;
            AddAudit(db, businessId, userId, "sales_quote", id, "sent", quote.SentAtUtc.Value, http.TraceIdentifier, new { quote.CurrentRevision, quote.ExpiryDate });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> AcceptQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        QuoteCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (quote.Status == SalesQuoteStatus.Accepted) return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
            if (quote.Status == SalesQuoteStatus.Expired) return QuoteExpiredResult();
            if (quote.Status != SalesQuoteStatus.Sent) return QuoteStateConflict();
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            var date = await GetDocumentCalendarAsync(db, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false);
            if (date.Error is not null) return date.Error;
            if (quote.ExpiryDate < date.Today)
            {
                quote.Status = SalesQuoteStatus.Expired;
                AddAudit(db, businessId, userId, "sales_quote", id, "expired", DateTimeOffset.UtcNow, http.TraceIdentifier, new { quote.CurrentRevision, quote.ExpiryDate });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return QuoteExpiredResult();
            }
            quote.Status = SalesQuoteStatus.Accepted;
            quote.AcceptedByUserId = userId;
            quote.AcceptedAtUtc = DateTimeOffset.UtcNow;
            AddAudit(db, businessId, userId, "sales_quote", id, "accepted", quote.AcceptedAtUtc.Value, http.TraceIdentifier, new { quote.CurrentRevision });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> RejectQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        QuoteCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var reason = NormalizeReason(request.Reason);
        if (reason is null) return InvalidReason();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (quote.Status == SalesQuoteStatus.Rejected) return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
            if (quote.Status != SalesQuoteStatus.Sent) return QuoteStateConflict();
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            quote.Status = SalesQuoteStatus.Rejected;
            AddAudit(db, businessId, userId, "sales_quote", id, "rejected", DateTimeOffset.UtcNow, http.TraceIdentifier, new { reason, quote.CurrentRevision });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> AmendQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        QuoteAmendRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var reason = NormalizeReason(request.Reason);
        if (reason is null) return InvalidReason();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (quote.Status is not (SalesQuoteStatus.Sent or SalesQuoteStatus.Accepted)
                || await db.SalesOrders.AnyAsync(order => order.BusinessId == businessId && order.SourceQuoteId == id, cancellationToken).ConfigureAwait(false)) return QuoteStateConflict();
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            var date = await GetDocumentCalendarAsync(db, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false);
            if (date.Error is not null) return date.Error;
            if (request.ExpiryDate < date.Today || request.ExpiryDate < quote.QuoteDate || (request.PromisedDeliveryDate is { } promised && promised < quote.QuoteDate)) return InvalidDates();
            if (request.Lines is null) return InvalidLines("Add at least one line to the amended quote.");
            var pricing = await PriceLinesAsync(db, businessId, ToLineInputs(request.Lines), await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (pricing.Error is not null) return pricing.Error;
            var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
            if (allowance is null) return BusinessScope.MissingMembership();
            var discountError = CheckDiscountAllowance(pricing.Lines, allowance.Value);
            if (discountError is not null) return discountError;
            quote.ExpiryDate = request.ExpiryDate;
            quote.PromisedDeliveryDate = request.PromisedDeliveryDate;
            ApplyDocumentAmounts(quote, pricing.Document!);
            var next = quote.CurrentRevision + 1;
            var snapshot = NewQuoteRevision(businessId, quote, pricing.Lines, next, userId, reason);
            db.SalesQuoteRevisions.Add(snapshot.Header);
            db.SalesQuoteRevisionLines.AddRange(snapshot.Lines);
            quote.CurrentRevision = next;
            quote.Status = SalesQuoteStatus.Sent;
            quote.SentByUserId = userId;
            quote.SentAtUtc = DateTimeOffset.UtcNow;
            quote.AcceptedByUserId = null;
            quote.AcceptedAtUtc = null;
            AddAudit(db, businessId, userId, "sales_quote", id, "amended_and_sent", quote.SentAtUtc.Value, http.TraceIdentifier, new { reason, quote.CurrentRevision });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> CancelQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        QuoteCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (quote.Status == SalesQuoteStatus.Cancelled) return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
            if (quote.Status is not (SalesQuoteStatus.Draft or SalesQuoteStatus.Sent or SalesQuoteStatus.Accepted)
                || await db.SalesOrders.AnyAsync(order => order.BusinessId == businessId && order.SourceQuoteId == id, cancellationToken).ConfigureAwait(false)) return QuoteStateConflict();
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            quote.Status = SalesQuoteStatus.Cancelled;
            AddAudit(db, businessId, userId, "sales_quote", id, "cancelled", DateTimeOffset.UtcNow, http.TraceIdentifier, new { reason = NormalizeReason(request.Reason), quote.CurrentRevision });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> ConvertQuoteAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        ConvertQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        if (!ValidIdempotencyKey(http.Request.Headers[IdempotencyHeader].ToString(), out var key)) return MissingIdempotencyKey();
        if (!ValidNote(request.Note)) return InvalidNote();
        var payloadHash = RequestFingerprint.Compute(new { quoteId = id, request });
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockIdempotencyKeyAsync(db, businessId, "quote-convert", key, cancellationToken).ConfigureAwait(false);
            var quote = await LockQuoteAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (quote is null) return NotFoundQuote();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            var keyOrder = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(order => order.BusinessId == businessId && order.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (keyOrder is not null && (keyOrder.SourceQuoteId != id || !string.Equals(keyOrder.PayloadHash, payloadHash, StringComparison.Ordinal))) return IdempotencyConflict();
            var existing = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(order => order.BusinessId == businessId && order.SourceQuoteId == id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.IdempotencyKey == key && !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)) return IdempotencyConflict();
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var orderDetail = await OrderDetailAsync(db, existing, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new QuoteConversionResultDto(await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false), orderDetail));
            }
            if (quote.Version != request.ExpectedVersion) return StaleVersion();
            if (quote.Status != SalesQuoteStatus.Accepted || quote.CurrentRevision <= 0) return QuoteNotAccepted();
            var date = await GetDocumentCalendarAsync(db, businessId, quote.LocationId, cancellationToken).ConfigureAwait(false);
            if (date.Error is not null) return date.Error;
            if (quote.ExpiryDate < date.Today) return QuoteExpiredResult();
            var revision = await db.SalesQuoteRevisions.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.QuoteId == quote.Id && row.RevisionNumber == quote.CurrentRevision, cancellationToken).ConfigureAwait(false);
            if (revision is null) return QuoteNotAccepted();
            var revisionLines = await db.SalesQuoteRevisionLines.AsNoTracking().Where(row => row.BusinessId == businessId && row.QuoteRevisionId == revision.Id).OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (revisionLines.Count == 0) return InvalidLines("The accepted quote revision has no lines.");
            var number = await AllocateDocumentNumberAsync(db, businessId, quote.LocationId, "sales-order", "SO-", date.Today.Year, cancellationToken).ConfigureAwait(false);
            var order = new SalesOrder
            {
                BusinessId = businessId,
                LocationId = quote.LocationId,
                CustomerId = quote.CustomerId,
                SourceQuoteId = quote.Id,
                SourceQuoteRevisionId = revision.Id,
                OrderNumber = number,
                Status = SalesOrderStatus.Draft,
                OrderDate = date.Today,
                PromisedDeliveryDate = revision.PromisedDeliveryDate,
                Currency = revision.Currency,
                SubtotalAmount = revision.SubtotalAmount,
                DiscountAmount = revision.DiscountAmount,
                TaxAmount = revision.TaxAmount,
                TotalAmount = revision.TotalAmount,
                RoundingAmount = revision.RoundingAmount,
                CreatedByUserId = userId,
                Note = NormalizeNote(request.Note) ?? revision.Note,
                IdempotencyKey = key,
                PayloadHash = payloadHash,
            };
            db.SalesOrders.Add(order);
            db.SalesOrderLines.AddRange(revisionLines.Select(line => new SalesOrderLine
            {
                BusinessId = businessId,
                OrderId = order.Id,
                LineKey = line.LineKey,
                LineNo = line.LineNo,
                VariantId = line.VariantId,
                Description = line.Description,
                SkuSnapshot = line.SkuSnapshot,
                QuantityOrdered = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountAmount = line.DiscountAmount,
                RevenueBaseAmount = line.RevenueBaseAmount,
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                LinePayableAmount = line.LinePayableAmount,
                TaxMode = line.TaxMode,
                PricingPolicyVersion = line.PricingPolicyVersion,
                SourceQuoteRevisionLineId = line.Id,
            }));
            AddAudit(db, businessId, userId, "sales_quote", quote.Id, "converted_to_order", DateTimeOffset.UtcNow, http.TraceIdentifier, new { order.Id, order.OrderNumber, revision.RevisionNumber });
            AddAudit(db, businessId, userId, "sales_order", order.Id, "created_from_quote", DateTimeOffset.UtcNow, http.TraceIdentifier, new { quote.Id, revision.RevisionNumber });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v1/sales-orders/{order.Id}", new QuoteConversionResultDto(
                await QuoteDetailAsync(db, quote, cancellationToken).ConfigureAwait(false),
                await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false)));
        });
    }

    private static async Task<IResult> ListOrdersAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? status,
        string? search,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var access = await GetLocationAccessAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
        if (access is null) return BusinessScope.MissingMembership();
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.SalesOrders.AsNoTracking().Where(row => row.BusinessId == businessId);
        if (!access.AllLocations) query = query.Where(row => db.BusinessMembershipLocations.Any(grant => grant.BusinessId == businessId && grant.MembershipId == access.MembershipId && grant.LocationId == row.LocationId));
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<SalesOrderStatus>(status.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
                return ApiResults.ValidationProblem("The order filter is invalid.", new Dictionary<string, string[]> { ["status"] = ["Choose a supported order status."] });
            query = query.Where(row => row.Status == parsed);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(row => EF.Functions.ILike(row.OrderNumber, pattern));
        }
        if (CursorPaging.TryDecode(cursor, out var cursorId)) query = query.Where(row => row.Id.CompareTo(cursorId) < 0);
        var rows = await query.OrderByDescending(row => row.Id).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        var items = rows.Select(ToOrderSummary).ToArray();
        return Results.Ok(new PagedResult<SalesOrderSummaryDto>(items, hasMore ? CursorPaging.Encode(rows[^1].Id) : null, size));
    }

    private static async Task<IResult> GetOrderAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var order = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (order is null) return NotFoundOrder();
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
        return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateOrderAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CreateSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
        if (!ValidIdempotencyKey(http.Request.Headers[IdempotencyHeader].ToString(), out var key)) return MissingIdempotencyKey();
        if (!ValidNote(request.Note)) return InvalidNote();
        var calendar = await GetDocumentCalendarAsync(db, businessId, request.LocationId, cancellationToken).ConfigureAwait(false);
        if (calendar.Error is not null) return calendar.Error;
        var orderDate = request.OrderDate ?? calendar.Today;
        if (request.PromisedDeliveryDate is { } promised && promised < orderDate) return InvalidDates();
        if (request.Lines is null) return InvalidLines("Add at least one line to the order.");
        var business = await GetBusinessAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        if (business is null) return BusinessScope.MissingMembership();
        var pricing = await PriceLinesAsync(db, businessId, ToLineInputs(request.Lines), await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (pricing.Error is not null) return pricing.Error;
        var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
        if (allowance is null) return BusinessScope.MissingMembership();
        var discountError = CheckDiscountAllowance(pricing.Lines, allowance.Value);
        if (discountError is not null) return discountError;
        if (request.CustomerId is { } customerId && !await CustomerExistsAsync(db, businessId, customerId, cancellationToken).ConfigureAwait(false)) return NotFoundCustomer();
        var payloadHash = RequestFingerprint.Compute(request);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockIdempotencyKeyAsync(db, businessId, "sales-order-create", key, cancellationToken).ConfigureAwait(false);
            var existing = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)) return IdempotencyConflict();
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(await OrderDetailAsync(db, existing, cancellationToken).ConfigureAwait(false));
            }
    
            var number = await AllocateDocumentNumberAsync(db, businessId, request.LocationId, "sales-order", "SO-", orderDate.Year, cancellationToken).ConfigureAwait(false);
            var order = new SalesOrder
            {
                BusinessId = businessId,
                LocationId = request.LocationId,
                CustomerId = request.CustomerId,
                OrderNumber = number,
                Status = SalesOrderStatus.Draft,
                OrderDate = orderDate,
                PromisedDeliveryDate = request.PromisedDeliveryDate,
                Currency = business.BaseCurrency,
                SubtotalAmount = pricing.Document!.GrossAmount,
                DiscountAmount = pricing.Document.DiscountAmount,
                TaxAmount = pricing.Document.TaxAmount,
                TotalAmount = pricing.Document.SettlementPayableAmount,
                RoundingAmount = pricing.Document.RoundingAmount,
                CreatedByUserId = userId,
                Note = NormalizeNote(request.Note),
                IdempotencyKey = key,
                PayloadHash = payloadHash,
            };
            db.SalesOrders.Add(order);
            db.SalesOrderLines.AddRange(pricing.Lines.Select(line => ToOrderLine(businessId, order.Id, line)));
            AddAudit(db, businessId, userId, "sales_order", order.Id, "draft_created", DateTimeOffset.UtcNow, http.TraceIdentifier, new { order.OrderNumber, order.TotalAmount });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v1/sales-orders/{order.Id}", await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> UpdateOrderAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UpdateSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (order is null) return NotFoundOrder();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (order.Status != SalesOrderStatus.Draft)
            {
                if (order.ConfirmedAtUtc is not null) return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
                return OrderDraftConflict();
            }
            if (order.Version != request.ExpectedVersion) return StaleVersion();
            if (!ValidNote(request.Note)) return InvalidNote();
            var calendar = await GetDocumentCalendarAsync(db, businessId, order.LocationId, cancellationToken).ConfigureAwait(false);
            if (calendar.Error is not null) return calendar.Error;
            if (request.PromisedDeliveryDate is { } promised && promised < order.OrderDate) return InvalidDates();
            if (request.Lines is null) return InvalidLines("Add at least one line to the order.");
            if (request.CustomerId is { } customerId && !await CustomerExistsAsync(db, businessId, customerId, cancellationToken).ConfigureAwait(false)) return NotFoundCustomer();
            var pricing = await PriceLinesAsync(db, businessId, ToLineInputs(request.Lines), await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (pricing.Error is not null) return pricing.Error;
            var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
            if (allowance is null) return BusinessScope.MissingMembership();
            var discountError = CheckDiscountAllowance(pricing.Lines, allowance.Value);
            if (discountError is not null) return discountError;
            order.CustomerId = request.CustomerId;
            order.PromisedDeliveryDate = request.PromisedDeliveryDate;
            order.Note = NormalizeNote(request.Note);
            ApplyDocumentAmounts(order, pricing.Document!);
            var oldLines = await db.SalesOrderLines.Where(line => line.BusinessId == businessId && line.OrderId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            db.SalesOrderLines.RemoveRange(oldLines);
            db.SalesOrderLines.AddRange(pricing.Lines.Select(line => ToOrderLine(businessId, id, line)));
            AddAudit(db, businessId, userId, "sales_order", id, "draft_updated", DateTimeOffset.UtcNow, http.TraceIdentifier, new { request.ExpectedVersion });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> ConfirmOrderAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        ConfirmSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (order is null) return NotFoundOrder();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (order.Status != SalesOrderStatus.Draft) return OrderDraftConflict();
            if (order.Version != request.ExpectedVersion) return StaleVersion();
            var lines = await db.SalesOrderLines.Where(line => line.BusinessId == businessId && line.OrderId == id).OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (lines.Count == 0) return InvalidLines("An order needs at least one line before it can be confirmed.");
            var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
            if (allowance is null) return BusinessScope.MissingMembership();
            var discountError = CheckDiscountAllowance(lines.Select(ToPricedLine).ToArray(), allowance.Value);
            if (discountError is not null) return discountError;
            if (order.SourceQuoteId is { } quoteId)
            {
                var sourceQuote = await db.SalesQuotes.AsNoTracking().SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == quoteId, cancellationToken).ConfigureAwait(false);
                if (sourceQuote is null || sourceQuote.Status != SalesQuoteStatus.Accepted || sourceQuote.CurrentRevision <= 0 || order.SourceQuoteRevisionId is null)
                    return QuoteNotAccepted();
                var sourceRevision = await db.SalesQuoteRevisions.AsNoTracking().AnyAsync(row => row.BusinessId == businessId && row.Id == order.SourceQuoteRevisionId && row.QuoteId == quoteId && row.RevisionNumber == sourceQuote.CurrentRevision, cancellationToken).ConfigureAwait(false);
                var quoteCalendar = await GetDocumentCalendarAsync(db, businessId, order.LocationId, cancellationToken).ConfigureAwait(false);
                if (quoteCalendar.Error is not null) return quoteCalendar.Error;
                if (!sourceRevision || sourceQuote.ExpiryDate < quoteCalendar.Today) return QuoteNotAccepted();
            }
            if (order.CustomerId is { } customerId)
            {
                await EnsureCreditLimitAsync(db, businessId, customerId, order.Currency, order.TotalAmount, null, cancellationToken).ConfigureAwait(false);
            }
            order.Status = SalesOrderStatus.Confirmed;
            order.ConfirmedByUserId = userId;
            order.ConfirmedAtUtc = DateTimeOffset.UtcNow;
            order.CurrentRevision = 1;
            var snapshot = NewOrderRevision(businessId, order, lines, 1, userId, NormalizeReason(request.Reason) ?? "Order confirmed");
            db.SalesOrderRevisions.Add(snapshot.Header);
            db.SalesOrderRevisionLines.AddRange(snapshot.Lines);
            AddAudit(db, businessId, userId, "sales_order", id, "confirmed", order.ConfirmedAtUtc.Value, http.TraceIdentifier, new { order.CurrentRevision, order.TotalAmount });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> AmendOrderAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        AmendSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var reason = NormalizeReason(request.Reason);
        if (reason is null) return InvalidReason();
        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines) return InvalidLines("Provide between one and 100 replacement outstanding lines.");
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (order is null) return NotFoundOrder();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (order.Status == SalesOrderStatus.Closed && order.RemainderCancelledAtUtc is not null) return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
            if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyFulfilled)) return OrderOpenConflict();
            if (order.Version != request.ExpectedVersion) return StaleVersion();
            if (request.PromisedDeliveryDate is { } promised && promised < order.OrderDate) return InvalidDates();
            var existingLines = await db.SalesOrderLines.Where(line => line.BusinessId == businessId && line.OrderId == id).OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            var lineByKey = existingLines.ToDictionary(line => line.LineKey);
            var supersededKeys = request.Lines.Where(line => line.SupersedesLineKey.HasValue).Select(line => line.SupersedesLineKey!.Value).ToArray();
            if (supersededKeys.Distinct().Count() != supersededKeys.Length || supersededKeys.Any(key => !lineByKey.ContainsKey(key)))
                return ApiResults.ValidationProblem("An amendment references an invalid order line.", new Dictionary<string, string[]> { ["lines.supersedesLineKey"] = ["Each superseded line must identify one existing line in this order, at most once."] });
            var pricing = await PriceLinesAsync(db, businessId, ToLineInputs(request.Lines), await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (pricing.Error is not null) return pricing.Error;
            var allowance = await GetDiscountAllowanceAsync(db, businessId, userId, cancellationToken).ConfigureAwait(false);
            if (allowance is null) return BusinessScope.MissingMembership();
            var discountError = CheckDiscountAllowance(pricing.Lines, allowance.Value);
            if (discountError is not null) return discountError;
    
            foreach (var line in existingLines)
            {
                var remaining = decimal.Round(line.QuantityOrdered - line.QuantityFulfilled - line.QuantityCancelled, 6, MidpointRounding.AwayFromZero);
                if (remaining > 0m) line.QuantityCancelled = decimal.Round(line.QuantityCancelled + remaining, 6, MidpointRounding.AwayFromZero);
            }
            var nextLineNo = existingLines.Count == 0 ? 1 : existingLines.Max(line => line.LineNo) + 1;
            var amendedLines = pricing.Lines.Select((line, index) => ToOrderLine(businessId, id, line, nextLineNo + index)).ToArray();
            order.PromisedDeliveryDate = request.PromisedDeliveryDate;
            var hasFulfilled = existingLines.Any(line => line.QuantityFulfilled > 0m);
            var liveLines = existingLines.Where(line => line.QuantityFulfilled > 0m).Select(AsFulfilledPricedLine).Concat(pricing.Lines).ToArray();
            var settlementScale = await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false);
            var revisedDocument = ComputeDocument(liveLines, settlementScale);
            ApplyDocumentAmounts(order, revisedDocument);
            order.Status = hasFulfilled ? SalesOrderStatus.PartiallyFulfilled : SalesOrderStatus.Confirmed;
            var revisionNumber = order.CurrentRevision + 1;
            order.CurrentRevision = revisionNumber;
            // Persist the version bump before inserting replacement lines so the database guard
            // can prove they belong to a versioned amendment in this transaction.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.SalesOrderLines.AddRange(amendedLines);
            var revision = NewOrderRevisionFromPriced(businessId, order, liveLines, revisionNumber, userId, reason);
            db.SalesOrderRevisions.Add(revision.Header);
            db.SalesOrderRevisionLines.AddRange(revision.Lines);
            AddAudit(db, businessId, userId, "sales_order", id, "amended", DateTimeOffset.UtcNow, http.TraceIdentifier, new { reason, order.CurrentRevision, fulfilledHistoricalLineCount = existingLines.Count(line => line.QuantityFulfilled > 0m) });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (order.CustomerId is { } customerId)
            {
                await EnsureCreditLimitAsync(db, businessId, customerId, order.Currency, order.TotalAmount, order.Id, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<IResult> CancelRemainderAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CancelOrderRemainderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var businessId, out var userId)) return BusinessScope.MissingMembership();
        var reason = NormalizeReason(request.Reason);
        if (reason is null) return InvalidReason();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await LockOrderAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
            if (order is null) return NotFoundOrder();
            if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, order.LocationId, cancellationToken).ConfigureAwait(false)) return ForbiddenLocation();
            if (order.Status is not (SalesOrderStatus.Confirmed or SalesOrderStatus.PartiallyFulfilled)) return OrderOpenConflict();
            if (order.Version != request.ExpectedVersion) return StaleVersion();
            var lines = await db.SalesOrderLines.Where(line => line.BusinessId == businessId && line.OrderId == id).OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var remaining = decimal.Round(line.QuantityOrdered - line.QuantityFulfilled - line.QuantityCancelled, 6, MidpointRounding.AwayFromZero);
                if (remaining > 0m) line.QuantityCancelled = decimal.Round(line.QuantityCancelled + remaining, 6, MidpointRounding.AwayFromZero);
            }
            var fulfilled = lines.Where(line => line.QuantityFulfilled > 0m).Select(AsFulfilledPricedLine).ToArray();
            var settlementScale = await GetSettlementScaleAsync(db, businessId, cancellationToken).ConfigureAwait(false);
            ApplyDocumentAmounts(order, ComputeDocument(fulfilled, settlementScale));
            order.Status = SalesOrderStatus.Closed;
            order.RemainderCancelledAtUtc = DateTimeOffset.UtcNow;
            order.RemainderCancelledByUserId = userId;
            order.RemainderCancellationReason = reason;
            var revisionNumber = order.CurrentRevision + 1;
            order.CurrentRevision = revisionNumber;
            var revision = NewOrderRevisionFromPriced(businessId, order, fulfilled, revisionNumber, userId, reason);
            db.SalesOrderRevisions.Add(revision.Header);
            db.SalesOrderRevisionLines.AddRange(revision.Lines);
            AddAudit(db, businessId, userId, "sales_order", id, "remainder_cancelled", order.RemainderCancelledAtUtc.Value, http.TraceIdentifier, new { reason, order.CurrentRevision });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(await OrderDetailAsync(db, order, cancellationToken).ConfigureAwait(false));
        });
    }

    private static async Task<LocationAccess?> GetLocationAccessAsync(BillingDbContext db, Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await db.BusinessMemberships.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == userId && row.IsActive)
            .Select(row => new LocationAccess(row.Id, row.AllLocations))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return membership;
    }

    private static bool TryGetActor(ClaimsPrincipal principal, out Guid businessId, out Guid userId)
    {
        businessId = Guid.Empty;
        userId = Guid.Empty;
        return principal.TryGetBusinessId(out businessId)
            && Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
    }

    private static async Task<SalesQuote?> LockQuoteAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
        => await db.SalesQuotes
            .FromSql($"SELECT * FROM sales_quotes WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static async Task<SalesOrder?> LockOrderAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
        => await db.SalesOrders
            .FromSql($"SELECT * FROM sales_orders WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static async Task LockIdempotencyKeyAsync(BillingDbContext db, Guid businessId, string scope, string key, CancellationToken cancellationToken)
    {
        var lockName = $"{businessId:N}:{scope}:{key}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> AllocateDocumentNumberAsync(
        BillingDbContext db,
        Guid businessId,
        Guid locationId,
        string kind,
        string prefix,
        int year,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO sales_document_sequences (id, business_id, location_id, document_kind, business_year, next_number, prefix, padding, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.CreateVersion7()}, {businessId}, {locationId}, {kind}, {year}, 1, {prefix}, 5, 1, now(), now())
            ON CONFLICT (business_id, location_id, document_kind, business_year) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        var sequence = await db.SalesDocumentSequences
            .FromSql($"SELECT * FROM sales_document_sequences WHERE business_id = {businessId} AND location_id = {locationId} AND document_kind = {kind} AND business_year = {year} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var number = sequence.NextNumber;
        sequence.NextNumber++;
        return $"{sequence.Prefix}{year.ToString(CultureInfo.InvariantCulture)}-{number.ToString($"D{sequence.Padding}", CultureInfo.InvariantCulture)}";
    }

    private static async Task<Business?> GetBusinessAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken)
        => await db.Businesses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == businessId && row.IsActive, cancellationToken).ConfigureAwait(false);

    private static async Task<DateCalendar> GetDocumentCalendarAsync(BillingDbContext db, Guid businessId, Guid locationId, CancellationToken cancellationToken)
    {
        var location = await db.BusinessLocations.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.Id == locationId && row.IsActive)
            .Select(row => new { row.TimeZone })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var business = await db.Businesses.AsNoTracking().Where(row => row.Id == businessId && row.IsActive).Select(row => new { row.TimeZone }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (location is null || business is null) return new(DateOnly.MinValue, ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The business location is unavailable."));
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(location.TimeZone) ? business.TimeZone : location.TimeZone);
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            return new(DateOnly.FromDateTime(local.DateTime), null);
        }
        catch (TimeZoneNotFoundException)
        {
            return new(DateOnly.MinValue, ApiResults.ValidationProblem("The business time zone is not configured correctly.", new Dictionary<string, string[]> { ["timeZone"] = ["Set a supported time-zone identifier before creating sales documents."] }));
        }
        catch (InvalidTimeZoneException)
        {
            return new(DateOnly.MinValue, ApiResults.ValidationProblem("The business time zone is not configured correctly.", new Dictionary<string, string[]> { ["timeZone"] = ["Set a supported time-zone identifier before creating sales documents."] }));
        }
    }

    private static async Task<int> GetSettlementScaleAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken)
        => await db.BusinessAccountingSettings.AsNoTracking().Where(row => row.BusinessId == businessId)
            .Select(row => (int?)row.SettlementScale).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 2;

    private static async Task<decimal?> GetDiscountAllowanceAsync(BillingDbContext db, Guid businessId, Guid userId, CancellationToken cancellationToken)
        => await db.BusinessMemberships.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == userId && row.IsActive)
            .Select(row => (decimal?)row.MaxDiscountRate)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static async Task<bool> CustomerExistsAsync(BillingDbContext db, Guid businessId, Guid customerId, CancellationToken cancellationToken)
        => await db.Customers.AsNoTracking().AnyAsync(row => row.BusinessId == businessId && row.Id == customerId && row.IsActive, cancellationToken).ConfigureAwait(false);

    private static async Task EnsureCreditLimitAsync(
        BillingDbContext db,
        Guid businessId,
        Guid customerId,
        string currency,
        decimal proposedExposure,
        Guid? excludingOrderId,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .FromSql($"SELECT * FROM customers WHERE business_id = {businessId} AND id = {customerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (customer is null) throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer does not exist in this business.", "The customer must be active in this business.");
        if (customer.CreditLimit is not { } creditLimit) return;

        var invoiceExposure = await db.Invoices.AsNoTracking().Where(row => row.BusinessId == businessId && row.CustomerId == customerId && row.Currency == currency
                && row.BalanceDue > 0m && (row.Status == InvoiceStatus.Issued || row.Status == InvoiceStatus.PartiallyPaid))
            .SumAsync(row => (decimal?)row.BalanceDue, cancellationToken).ConfigureAwait(false) ?? 0m;
        var saleExposure = await db.Sales.AsNoTracking().Where(row => row.BusinessId == businessId && row.CustomerId == customerId && row.Currency == currency
                && row.Status == SaleStatus.Posted && (row.PaymentStatus == PaymentStatus.Pending || row.PaymentStatus == PaymentStatus.Bounced)
                && !db.Invoices.Any(invoice => invoice.BusinessId == businessId && invoice.SourceSaleId == row.Id
                    && (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.PartiallyPaid || invoice.Status == InvoiceStatus.Paid)))
            .SumAsync(row => (decimal?)row.TotalAmount, cancellationToken).ConfigureAwait(false) ?? 0m;
        var openingExposure = await db.OpenItems.AsNoTracking().Where(row => row.BusinessId == businessId && row.CustomerId == customerId && row.Currency == currency && row.Kind == OpenItemKind.Receivable)
            .SumAsync(row => (decimal?)row.OriginalAmount, cancellationToken).ConfigureAwait(false) ?? 0m;
        var committedOrders = db.SalesOrders.AsNoTracking().Where(row => row.BusinessId == businessId && row.CustomerId == customerId && row.Currency == currency
            && (row.Status == SalesOrderStatus.Confirmed || row.Status == SalesOrderStatus.PartiallyFulfilled));
        if (excludingOrderId is { } excluded) committedOrders = committedOrders.Where(row => row.Id != excluded);
        var orderExposure = await committedOrders.SumAsync(row => (decimal?)row.TotalAmount, cancellationToken).ConfigureAwait(false) ?? 0m;
        var current = LedgerWriter.Round(invoiceExposure + saleExposure + openingExposure + orderExposure);
        var projected = LedgerWriter.Round(current + proposedExposure);
        if (projected > creditLimit)
        {
            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.CreditLimitExceeded,
                "The customer credit limit would be exceeded.",
                $"Open receivables and commitments are {current} {currency}; this order would raise them to {projected} against a limit of {creditLimit} {currency}.");
        }
    }

    private static LineInput[] ToLineInputs(IReadOnlyList<QuoteLineRequest> lines)
        => lines.Select(line => new LineInput(line.VariantId, line.Description, line.Quantity, line.UnitPrice, line.DiscountAmount, line.TaxRateId, line.TaxMode)).ToArray();

    private static LineInput[] ToLineInputs(IReadOnlyList<SalesOrderLineRequest> lines)
        => lines.Select(line => new LineInput(line.VariantId, line.Description, line.Quantity, line.UnitPrice, line.DiscountAmount, line.TaxRateId, line.TaxMode)).ToArray();

    private static LineInput[] ToLineInputs(IReadOnlyList<SalesOrderAmendmentLineRequest> lines)
        => lines.Select(line => new LineInput(line.VariantId, line.Description, line.QuantityOutstanding, line.UnitPrice, line.DiscountAmount, line.TaxRateId, line.TaxMode, line.SupersedesLineKey)).ToArray();

    private static async Task<PricingResult> PriceLinesAsync(
        BillingDbContext db,
        Guid businessId,
        IReadOnlyList<LineInput> inputs,
        int settlementScale,
        CancellationToken cancellationToken)
    {
        if (inputs.Count is 0 or > MaxLines) return new([], null, InvalidLines("Provide between one and 100 lines."));
        var errors = new Dictionary<string, string[]>();
        var variantIds = inputs.Where(input => input.VariantId.HasValue).Select(input => input.VariantId!.Value).Distinct().ToArray();
        var variants = variantIds.Length == 0
            ? new Dictionary<Guid, VariantPricing>()
            : await (from variant in db.ProductVariants.AsNoTracking()
                     join product in db.Products.AsNoTracking() on new { variant.ProductId, variant.BusinessId } equals new { ProductId = product.Id, product.BusinessId }
                     where variant.BusinessId == businessId && variantIds.Contains(variant.Id)
                     select new VariantPricing(variant.Id, variant.Name, variant.Sku, variant.SellingPrice, product.TaxRateId, product.TaxType, product.IsActive, variant.IsActive))
                .ToDictionaryAsync(row => row.Id, cancellationToken).ConfigureAwait(false);
        var missingVariant = variantIds.FirstOrDefault(id => !variants.ContainsKey(id));
        if (missingVariant != Guid.Empty) return new([], null, NotFoundVariant());

        var rateIds = inputs.Where(input => !input.VariantId.HasValue && input.TaxRateId.HasValue).Select(input => input.TaxRateId!.Value)
            .Concat(variants.Values.Where(row => row.TaxRateId.HasValue).Select(row => row.TaxRateId!.Value)).Distinct().ToArray();
        var rates = rateIds.Length == 0
            ? new Dictionary<Guid, decimal>()
            : await db.TaxRates.AsNoTracking().Where(row => row.BusinessId == businessId && row.IsActive && rateIds.Contains(row.Id))
                .ToDictionaryAsync(row => row.Id, row => row.Rate, cancellationToken).ConfigureAwait(false);
        var missingRate = rateIds.FirstOrDefault(id => !rates.ContainsKey(id));
        if (missingRate != Guid.Empty) return new([], null, ApiResults.ValidationProblem("A selected tax rate is unavailable.", new Dictionary<string, string[]> { ["taxRateId"] = ["Select an active tax rate from this business."] }));

        var calculations = new List<CommercialLineInput>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var field = $"lines[{index}]";
            var description = input.Description?.Trim() ?? string.Empty;
            if (input.VariantId is { } variantId)
            {
                var variant = variants[variantId];
                if (!variant.IsActive || !variant.ProductIsActive) { errors[$"{field}.variantId"] = ["The product or variant is inactive."]; continue; }
                if (input.UnitPrice is { } requestedPrice && requestedPrice != variant.SellingPrice)
                    return new([], null, ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.InvalidPrice, "The requested price does not match the catalog.", $"Line {index + 1} must use the current catalog price; price overrides require approval."));
                if (input.TaxRateId is { } requestedRate && requestedRate != variant.TaxRateId)
                    { errors[$"{field}.taxRateId"] = ["A product line must use the tax rate configured for its product."]; continue; }
                var productTaxMode = variant.TaxType == TaxType.Inclusive ? CommercialTaxMode.Inclusive : CommercialTaxMode.Exclusive;
                if (!string.IsNullOrWhiteSpace(input.TaxMode))
                {
                    try
                    {
                        if (CommercialAmountCalculator.ParseTaxMode(input.TaxMode, $"{field}.taxMode") != productTaxMode)
                            { errors[$"{field}.taxMode"] = ["A product line must use the tax mode configured for its product."]; continue; }
                    }
                    catch (CommercialAmountException exception) { errors[exception.Field] = [exception.Message]; continue; }
                }
                if (string.IsNullOrWhiteSpace(description)) description = variant.Name;
                if (description.Length > 300) { errors[$"{field}.description"] = ["Descriptions may not exceed 300 characters."]; continue; }
                var price = variant.SellingPrice;
                var taxRate = variant.TaxRateId is { } productRateId ? rates[productRateId] : 0m;
                calculations.Add(new CommercialLineInput { Quantity = input.Quantity, UnitPrice = price, TaxMode = productTaxMode, TaxRate = taxRate, DiscountAmount = input.DiscountAmount, PricingPolicyVersion = "catalog-v1" });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(description)) { errors[$"{field}.description"] = ["A service line requires a description."]; continue; }
                if (description.Length > 300) { errors[$"{field}.description"] = ["Descriptions may not exceed 300 characters."]; continue; }
                if (input.UnitPrice is not { } price) { errors[$"{field}.unitPrice"] = ["A service line requires an explicit unit price."]; continue; }
                var taxRate = input.TaxRateId is { } serviceRateId ? rates[serviceRateId] : 0m;
                CommercialTaxMode taxMode;
                try { taxMode = CommercialAmountCalculator.ParseTaxMode(input.TaxMode, $"{field}.taxMode"); }
                catch (CommercialAmountException exception) { errors[exception.Field] = [exception.Message]; continue; }
                calculations.Add(new CommercialLineInput { Quantity = input.Quantity, UnitPrice = price, TaxMode = taxMode, TaxRate = taxRate, DiscountAmount = input.DiscountAmount, PricingPolicyVersion = "service-v1" });
            }
        }
        if (errors.Count > 0) return new([], null, ApiResults.ValidationProblem("The sales document contains invalid lines.", errors));

        CommercialDocumentAmounts document;
        try { document = CommercialAmountCalculator.ComputeDocument(calculations, settlementScale); }
        catch (CommercialAmountException exception) { return new([], null, ApiResults.ValidationProblem("The sales document amount is invalid.", new Dictionary<string, string[]> { [exception.Field] = [exception.Message] })); }

        var priced = new List<PricedLine>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var amount = document.Lines[index];
            var calculation = calculations[index];
            if (input.VariantId is { } variantId)
            {
                var variant = variants[variantId];
                priced.Add(new PricedLine(Guid.CreateVersion7(), index + 1, variantId, string.IsNullOrWhiteSpace(input.Description) ? variant.Name : input.Description.Trim(), variant.Sku,
                    calculation.Quantity, calculation.UnitPrice, amount.DiscountAmount, amount.RevenueBaseAmount, amount.TaxRate, amount.TaxAmount, amount.PayableAmount,
                    amount.GrossAmount, amount.TaxMode.ToString(), calculation.PricingPolicyVersion ?? "catalog-v1", false));
            }
            else
            {
                priced.Add(new PricedLine(Guid.CreateVersion7(), index + 1, null, input.Description.Trim(), null, calculation.Quantity, calculation.UnitPrice, amount.DiscountAmount,
                    amount.RevenueBaseAmount, amount.TaxRate, amount.TaxAmount, amount.PayableAmount, amount.GrossAmount, amount.TaxMode.ToString(), calculation.PricingPolicyVersion ?? "service-v1", false));
            }
        }
        return new(priced, document, null);
    }

    private static IResult? CheckDiscountAllowance(IReadOnlyList<PricedLine> lines, decimal allowance)
    {
        foreach (var line in lines)
        {
            if (line.GrossAmount > 0m && line.DiscountAmount / line.GrossAmount > allowance)
            {
                return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.ApprovalRequired,
                    "The discount exceeds the current user's approval limit.",
                    $"Line {line.LineNo} exceeds the member's authorized discount rate. Ask an authorized manager to approve the commercial terms.");
            }
        }
        return null;
    }

    private static SalesQuoteLine ToQuoteLine(Guid businessId, Guid quoteId, PricedLine line)
        => new()
        {
            BusinessId = businessId,
            QuoteId = quoteId,
            LineKey = line.LineKey,
            LineNo = line.LineNo,
            VariantId = line.VariantId,
            Description = line.Description,
            SkuSnapshot = line.SkuSnapshot,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            RevenueBaseAmount = line.RevenueBaseAmount,
            TaxRate = line.TaxRate,
            TaxAmount = line.TaxAmount,
            LinePayableAmount = line.LinePayableAmount,
            TaxMode = line.TaxMode,
            PricingPolicyVersion = line.PricingPolicyVersion,
        };

    private static SalesOrderLine ToOrderLine(Guid businessId, Guid orderId, PricedLine line, int? lineNo = null)
        => new()
        {
            BusinessId = businessId,
            OrderId = orderId,
            LineKey = line.LineKey,
            LineNo = lineNo ?? line.LineNo,
            VariantId = line.VariantId,
            Description = line.Description,
            SkuSnapshot = line.SkuSnapshot,
            QuantityOrdered = line.Quantity,
            QuantityFulfilled = line.IsHistoricalFulfillment ? line.Quantity : 0m,
            QuantityCancelled = 0m,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            RevenueBaseAmount = line.RevenueBaseAmount,
            TaxRate = line.TaxRate,
            TaxAmount = line.TaxAmount,
            LinePayableAmount = line.LinePayableAmount,
            TaxMode = line.TaxMode,
            PricingPolicyVersion = line.PricingPolicyVersion,
        };

    private static PricedLine ToPricedLine(SalesQuoteLine line)
        => new(line.LineKey, line.LineNo, line.VariantId, line.Description, line.SkuSnapshot, line.Quantity, line.UnitPrice,
            line.DiscountAmount, line.RevenueBaseAmount, line.TaxRate, line.TaxAmount, line.LinePayableAmount,
            LedgerWriter.Round(line.Quantity * line.UnitPrice), line.TaxMode, line.PricingPolicyVersion, false);

    private static PricedLine ToPricedLine(SalesQuoteRevisionLine line)
        => new(line.LineKey, line.LineNo, line.VariantId, line.Description, line.SkuSnapshot, line.Quantity, line.UnitPrice,
            line.DiscountAmount, line.RevenueBaseAmount, line.TaxRate, line.TaxAmount, line.LinePayableAmount,
            LedgerWriter.Round(line.Quantity * line.UnitPrice), line.TaxMode, line.PricingPolicyVersion, false);

    private static PricedLine ToPricedLine(SalesOrderLine line)
        => new(line.LineKey, line.LineNo, line.VariantId, line.Description, line.SkuSnapshot, line.QuantityOrdered, line.UnitPrice,
            line.DiscountAmount, line.RevenueBaseAmount, line.TaxRate, line.TaxAmount, line.LinePayableAmount,
            LedgerWriter.Round(line.QuantityOrdered * line.UnitPrice), line.TaxMode, line.PricingPolicyVersion, false);

    private static PricedLine AsFulfilledPricedLine(SalesOrderLine line)
    {
        var fraction = line.QuantityFulfilled / line.QuantityOrdered;
        var discount = LedgerWriter.Round(line.DiscountAmount * fraction);
        var taxMode = CommercialAmountCalculator.ParseTaxMode(line.TaxMode, "taxMode");
        var amount = CommercialAmountCalculator.ComputeLine(line.QuantityFulfilled, line.UnitPrice, taxMode, line.TaxRate, discount, line.PricingPolicyVersion);
        return new PricedLine(line.LineKey, line.LineNo, line.VariantId, line.Description, line.SkuSnapshot, line.QuantityFulfilled,
            line.UnitPrice, amount.DiscountAmount, amount.RevenueBaseAmount, amount.TaxRate, amount.TaxAmount,
            amount.PayableAmount, amount.GrossAmount, amount.TaxMode.ToString(), amount.PricingPolicyVersion ?? line.PricingPolicyVersion, true);
    }

    private static CommercialDocumentAmounts ComputeDocument(IReadOnlyList<PricedLine> lines, int settlementScale)
        => CommercialAmountCalculator.ComputeDocument(lines.Select(line => new CommercialLineInput
        {
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            TaxMode = CommercialAmountCalculator.ParseTaxMode(line.TaxMode),
            TaxRate = line.TaxRate,
            DiscountAmount = line.DiscountAmount,
            PricingPolicyVersion = line.PricingPolicyVersion,
        }).ToArray(), settlementScale);

    private static void ApplyDocumentAmounts(SalesQuote quote, CommercialDocumentAmounts amounts)
    {
        quote.SubtotalAmount = amounts.GrossAmount;
        quote.DiscountAmount = amounts.DiscountAmount;
        quote.TaxAmount = amounts.TaxAmount;
        quote.TotalAmount = amounts.SettlementPayableAmount;
        quote.RoundingAmount = amounts.RoundingAmount;
    }

    private static void ApplyDocumentAmounts(SalesOrder order, CommercialDocumentAmounts amounts)
    {
        order.SubtotalAmount = amounts.GrossAmount;
        order.DiscountAmount = amounts.DiscountAmount;
        order.TaxAmount = amounts.TaxAmount;
        order.TotalAmount = amounts.SettlementPayableAmount;
        order.RoundingAmount = amounts.RoundingAmount;
    }

    private static QuoteRevisionBuild NewQuoteRevision(
        Guid businessId,
        SalesQuote quote,
        IReadOnlyList<SalesQuoteLine> lines,
        int revisionNumber,
        Guid actorId,
        string reason)
        => NewQuoteRevision(businessId, quote, lines.Select(ToPricedLine).ToArray(), revisionNumber, actorId, reason);

    private static QuoteRevisionBuild NewQuoteRevision(
        Guid businessId,
        SalesQuote quote,
        IReadOnlyList<PricedLine> lines,
        int revisionNumber,
        Guid actorId,
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var revisionId = Guid.CreateVersion7();
        var header = new SalesQuoteRevision
        {
            Id = revisionId,
            BusinessId = businessId,
            QuoteId = quote.Id,
            RevisionNumber = revisionNumber,
            QuoteDate = quote.QuoteDate,
            ExpiryDate = quote.ExpiryDate,
            PromisedDeliveryDate = quote.PromisedDeliveryDate,
            Currency = quote.Currency,
            SubtotalAmount = quote.SubtotalAmount,
            DiscountAmount = quote.DiscountAmount,
            TaxAmount = quote.TaxAmount,
            TotalAmount = quote.TotalAmount,
            RoundingAmount = quote.RoundingAmount,
            Note = quote.Note,
            Reason = reason,
            ChangedByUserId = actorId,
            RecordedAtUtc = now,
        };
        var revisionLines = lines.Select((line, index) => new SalesQuoteRevisionLine
        {
            BusinessId = businessId,
            QuoteRevisionId = revisionId,
            LineKey = line.LineKey,
            LineNo = index + 1,
            VariantId = line.VariantId,
            Description = line.Description,
            SkuSnapshot = line.SkuSnapshot,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            RevenueBaseAmount = line.RevenueBaseAmount,
            TaxRate = line.TaxRate,
            TaxAmount = line.TaxAmount,
            LinePayableAmount = line.LinePayableAmount,
            TaxMode = line.TaxMode,
            PricingPolicyVersion = line.PricingPolicyVersion,
        }).ToArray();
        return new(header, revisionLines);
    }

    private static OrderRevisionBuild NewOrderRevision(
        Guid businessId,
        SalesOrder order,
        IReadOnlyList<SalesOrderLine> lines,
        int revisionNumber,
        Guid actorId,
        string reason)
    {
        var revisionId = Guid.CreateVersion7();
        var header = NewOrderRevisionHeader(businessId, order, revisionId, revisionNumber, actorId, reason);
        var snapshots = lines.Select((line, index) => new SalesOrderRevisionLine
        {
            BusinessId = businessId,
            OrderRevisionId = revisionId,
            LineKey = line.LineKey,
            LineNo = index + 1,
            VariantId = line.VariantId,
            Description = line.Description,
            SkuSnapshot = line.SkuSnapshot,
            QuantityOrdered = line.QuantityOrdered,
            QuantityFulfilled = line.QuantityFulfilled,
            QuantityCancelled = line.QuantityCancelled,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            RevenueBaseAmount = line.RevenueBaseAmount,
            TaxRate = line.TaxRate,
            TaxAmount = line.TaxAmount,
            LinePayableAmount = line.LinePayableAmount,
            TaxMode = line.TaxMode,
            PricingPolicyVersion = line.PricingPolicyVersion,
        }).ToArray();
        return new(header, snapshots);
    }

    private static OrderRevisionBuild NewOrderRevisionFromPriced(
        Guid businessId,
        SalesOrder order,
        IReadOnlyList<PricedLine> lines,
        int revisionNumber,
        Guid actorId,
        string reason)
    {
        var revisionId = Guid.CreateVersion7();
        var header = NewOrderRevisionHeader(businessId, order, revisionId, revisionNumber, actorId, reason);
        var snapshots = lines.Select((line, index) => new SalesOrderRevisionLine
        {
            BusinessId = businessId,
            OrderRevisionId = revisionId,
            LineKey = line.LineKey,
            LineNo = index + 1,
            VariantId = line.VariantId,
            Description = line.Description,
            SkuSnapshot = line.SkuSnapshot,
            QuantityOrdered = line.Quantity,
            QuantityFulfilled = line.IsHistoricalFulfillment ? line.Quantity : 0m,
            QuantityCancelled = 0m,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            RevenueBaseAmount = line.RevenueBaseAmount,
            TaxRate = line.TaxRate,
            TaxAmount = line.TaxAmount,
            LinePayableAmount = line.LinePayableAmount,
            TaxMode = line.TaxMode,
            PricingPolicyVersion = line.PricingPolicyVersion,
        }).ToArray();
        return new(header, snapshots);
    }

    private static SalesOrderRevision NewOrderRevisionHeader(Guid businessId, SalesOrder order, Guid revisionId, int revisionNumber, Guid actorId, string reason)
        => new()
        {
            Id = revisionId,
            BusinessId = businessId,
            OrderId = order.Id,
            RevisionNumber = revisionNumber,
            OrderDate = order.OrderDate,
            PromisedDeliveryDate = order.PromisedDeliveryDate,
            Currency = order.Currency,
            SubtotalAmount = order.SubtotalAmount,
            DiscountAmount = order.DiscountAmount,
            TaxAmount = order.TaxAmount,
            TotalAmount = order.TotalAmount,
            RoundingAmount = order.RoundingAmount,
            Reason = reason,
            ChangedByUserId = actorId,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };

    private static async Task<SalesQuoteDto> QuoteDetailAsync(BillingDbContext db, SalesQuote quote, CancellationToken cancellationToken)
    {
        var revisions = await db.SalesQuoteRevisions.AsNoTracking().Where(row => row.BusinessId == quote.BusinessId && row.QuoteId == quote.Id)
            .OrderByDescending(row => row.RevisionNumber).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        revisions.Reverse();
        OrderLineDto[] currentLines;
        if (quote.Status == SalesQuoteStatus.Draft || revisions.Count == 0)
        {
            var rows = await db.SalesQuoteLines.AsNoTracking().Where(row => row.BusinessId == quote.BusinessId && row.QuoteId == quote.Id)
                .OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            currentLines = rows.Select(ToOrderLineDto).ToArray();
        }
        else
        {
            var currentRevisionId = revisions[revisions.Count - 1].Id;
            var lineRows = await db.SalesQuoteRevisionLines.AsNoTracking().Where(row => row.BusinessId == quote.BusinessId && row.QuoteRevisionId == currentRevisionId)
                .OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
            currentLines = lineRows.Select(ToOrderLineDto).ToArray();
        }
        var revisionIds = revisions.Select(row => row.Id).ToArray();
        var allLines = revisionIds.Length == 0 ? [] : await db.SalesQuoteRevisionLines.AsNoTracking()
            .Where(row => row.BusinessId == quote.BusinessId && revisionIds.Contains(row.QuoteRevisionId)).OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byRevision = allLines.GroupBy(row => row.QuoteRevisionId).ToDictionary(group => group.Key, group => (IReadOnlyList<OrderLineDto>)group.Select(ToOrderLineDto).ToArray());
        return new SalesQuoteDto(quote.Id, quote.LocationId, quote.CustomerId, quote.QuoteNumber, quote.Status.ToString(), quote.QuoteDate,
            quote.ExpiryDate, quote.PromisedDeliveryDate, quote.Currency, quote.SubtotalAmount, quote.DiscountAmount, quote.TaxAmount,
            quote.TotalAmount, quote.RoundingAmount, quote.CurrentRevision, quote.Version, quote.CreatedByUserId, quote.CreatedAtUtc,
            quote.SentAtUtc, quote.AcceptedAtUtc, quote.Note, currentLines,
            revisions.Select(row => new QuoteRevisionDto(row.Id, row.RevisionNumber, row.QuoteDate, row.ExpiryDate, row.PromisedDeliveryDate,
                row.Currency, row.SubtotalAmount, row.DiscountAmount, row.TaxAmount, row.TotalAmount, row.RoundingAmount, row.Note, row.Reason,
                row.ChangedByUserId, row.RecordedAtUtc, byRevision.GetValueOrDefault(row.Id, Array.Empty<OrderLineDto>()))).ToArray());
    }

    private static async Task<SalesOrderDto> OrderDetailAsync(BillingDbContext db, SalesOrder order, CancellationToken cancellationToken)
    {
        var lines = await db.SalesOrderLines.AsNoTracking().Where(row => row.BusinessId == order.BusinessId && row.OrderId == order.Id)
            .OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var revisions = await db.SalesOrderRevisions.AsNoTracking().Where(row => row.BusinessId == order.BusinessId && row.OrderId == order.Id)
            .OrderByDescending(row => row.RevisionNumber).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        revisions.Reverse();
        var revisionIds = revisions.Select(row => row.Id).ToArray();
        var allLines = revisionIds.Length == 0 ? [] : await db.SalesOrderRevisionLines.AsNoTracking()
            .Where(row => row.BusinessId == order.BusinessId && revisionIds.Contains(row.OrderRevisionId)).OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byRevision = allLines.GroupBy(row => row.OrderRevisionId).ToDictionary(group => group.Key, group => (IReadOnlyList<OrderLineDto>)group.Select(ToOrderLineDto).ToArray());
        return new SalesOrderDto(order.Id, order.LocationId, order.CustomerId, order.SourceQuoteId, order.SourceQuoteRevisionId,
            order.OrderNumber, order.Status.ToString(), order.OrderDate, order.PromisedDeliveryDate, order.Currency, order.SubtotalAmount,
            order.DiscountAmount, order.TaxAmount, order.TotalAmount, order.RoundingAmount, order.CurrentRevision, order.Version,
            order.CreatedByUserId, order.CreatedAtUtc, order.ConfirmedAtUtc, order.RemainderCancelledAtUtc,
            order.RemainderCancellationReason, order.Note, lines.Select(ToOrderLineDto).ToArray(),
            revisions.Select(row => new SalesOrderRevisionDto(row.Id, row.RevisionNumber, row.OrderDate, row.PromisedDeliveryDate,
                row.Currency, row.SubtotalAmount, row.DiscountAmount, row.TaxAmount, row.TotalAmount, row.RoundingAmount, row.Reason,
                row.ChangedByUserId, row.RecordedAtUtc, byRevision.GetValueOrDefault(row.Id, Array.Empty<OrderLineDto>()))).ToArray());
    }

    private static OrderLineDto ToOrderLineDto(SalesQuoteLine row)
        => new(row.Id, row.LineKey, row.LineNo, row.VariantId, row.Description, row.SkuSnapshot, row.Quantity, null, null,
            row.UnitPrice, row.DiscountAmount, row.RevenueBaseAmount, row.TaxRate, row.TaxAmount, row.LinePayableAmount, row.TaxMode, row.PricingPolicyVersion);

    private static OrderLineDto ToOrderLineDto(SalesQuoteRevisionLine row)
        => new(row.Id, row.LineKey, row.LineNo, row.VariantId, row.Description, row.SkuSnapshot, row.Quantity, null, null,
            row.UnitPrice, row.DiscountAmount, row.RevenueBaseAmount, row.TaxRate, row.TaxAmount, row.LinePayableAmount, row.TaxMode, row.PricingPolicyVersion);

    private static OrderLineDto ToOrderLineDto(SalesOrderLine row)
        => new(row.Id, row.LineKey, row.LineNo, row.VariantId, row.Description, row.SkuSnapshot, row.QuantityOrdered, row.QuantityFulfilled,
            row.QuantityCancelled, row.UnitPrice, row.DiscountAmount, row.RevenueBaseAmount, row.TaxRate, row.TaxAmount,
            row.LinePayableAmount, row.TaxMode, row.PricingPolicyVersion);

    private static OrderLineDto ToOrderLineDto(SalesOrderRevisionLine row)
        => new(row.Id, row.LineKey, row.LineNo, row.VariantId, row.Description, row.SkuSnapshot, row.QuantityOrdered, row.QuantityFulfilled,
            row.QuantityCancelled, row.UnitPrice, row.DiscountAmount, row.RevenueBaseAmount, row.TaxRate, row.TaxAmount,
            row.LinePayableAmount, row.TaxMode, row.PricingPolicyVersion);

    private static SalesQuoteSummaryDto ToQuoteSummary(SalesQuote row)
        => new(row.Id, row.LocationId, row.CustomerId, row.QuoteNumber, row.Status.ToString(), row.QuoteDate, row.ExpiryDate,
            row.PromisedDeliveryDate, row.Currency, row.TotalAmount, row.CurrentRevision, row.Version);

    private static SalesOrderSummaryDto ToOrderSummary(SalesOrder row)
        => new(row.Id, row.LocationId, row.CustomerId, row.SourceQuoteId, row.OrderNumber, row.Status.ToString(), row.OrderDate,
            row.PromisedDeliveryDate, row.Currency, row.TotalAmount, row.CurrentRevision, row.Version);

    private static void AddAudit(BillingDbContext db, Guid businessId, Guid userId, string entityType, Guid entityId, string action,
        DateTimeOffset occurredAt, string correlationId, object summary)
        => db.AuditEvents.Add(new AuditEvent
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OccurredAtUtc = occurredAt,
            CorrelationId = correlationId,
            Summary = JsonSerializer.Serialize(summary, ContractJson.Options),
        });

    private static bool ValidIdempotencyKey(string raw, out string key)
    {
        key = raw.Trim();
        return key.Length is > 0 and <= 64;
    }

    private static string? NormalizeReason(string? reason)
    {
        var value = reason?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > MaxReasonLength ? null : value;
    }

    private static string? NormalizeNote(string? note)
    {
        var value = note?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value.Length <= MaxNoteLength ? value : null;
    }

    private static bool ValidNote(string? note) => string.IsNullOrWhiteSpace(note) || note.Trim().Length <= MaxNoteLength;

    private static IResult MissingIdempotencyKey()
        => ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1 to 64 characters."] });

    private static IResult IdempotencyConflict()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.SameKeyDifferentPayload, "The idempotency key was used for a different request.");

    private static IResult InvalidDates()
        => ApiResults.ValidationProblem("The sales document dates are invalid.", new Dictionary<string, string[]> { ["dates"] = ["Expiry and promised delivery dates cannot precede the document date."] });

    private static IResult InvalidLines(string detail)
        => ApiResults.ValidationProblem(detail, new Dictionary<string, string[]> { ["lines"] = [detail] });

    private static IResult InvalidReason()
        => ApiResults.ValidationProblem("A reason is required.", new Dictionary<string, string[]> { ["reason"] = ["Enter a reason of 1 to 500 characters."] });

    private static IResult InvalidNote()
        => ApiResults.ValidationProblem("The note is too long.", new Dictionary<string, string[]> { ["note"] = [$"Notes may not exceed {MaxNoteLength} characters."] });

    private static IResult StaleVersion()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.OptimisticConcurrencyFailure, "The document changed since it was loaded.", "Reload the latest version and retry the command.");

    private static IResult QuoteStateConflict()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.QuoteNotDraft, "The quote is not in a state that allows this command.");

    private static IResult QuoteExpiredResult()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.QuoteExpired, "The quote has expired and cannot be accepted or converted.");

    private static IResult QuoteNotAccepted()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.QuoteNotAccepted, "The current quote revision is not accepted.");

    private static IResult OrderDraftConflict()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.SalesOrderNotDraft, "The order is not a draft.");

    private static IResult OrderOpenConflict()
        => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.SalesOrderNotOpen, "The order has no cancellable or amendable remainder.");

    private static IResult NotFoundQuote()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The quote does not exist in this business.");

    private static IResult NotFoundOrder()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The sales order does not exist in this business.");

    private static IResult NotFoundCustomer()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The customer does not exist in this business.");

    private static IResult NotFoundVariant()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "A selected product variant does not exist in this business.");

    private static IResult ForbiddenLocation()
        => ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "The membership cannot access this location.");

    private sealed record LocationAccess(Guid MembershipId, bool AllLocations);
    private sealed record DateCalendar(DateOnly Today, IResult? Error);
    private sealed record VariantPricing(Guid Id, string Name, string? Sku, decimal SellingPrice, Guid? TaxRateId, TaxType TaxType, bool ProductIsActive, bool IsActive);
    private sealed record LineInput(Guid? VariantId, string Description, decimal Quantity, decimal? UnitPrice, decimal DiscountAmount, Guid? TaxRateId, string? TaxMode, Guid? SupersedesLineKey = null);
    private sealed record PricingResult(IReadOnlyList<PricedLine> Lines, CommercialDocumentAmounts? Document, IResult? Error);
    private sealed record QuoteRevisionBuild(SalesQuoteRevision Header, IReadOnlyList<SalesQuoteRevisionLine> Lines);
    private sealed record OrderRevisionBuild(SalesOrderRevision Header, IReadOnlyList<SalesOrderRevisionLine> Lines);
    private sealed record PricedLine(
        Guid LineKey,
        int LineNo,
        Guid? VariantId,
        string Description,
        string? SkuSnapshot,
        decimal Quantity,
        decimal UnitPrice,
        decimal DiscountAmount,
        decimal RevenueBaseAmount,
        decimal TaxRate,
        decimal TaxAmount,
        decimal LinePayableAmount,
        decimal GrossAmount,
        string TaxMode,
        string PricingPolicyVersion,
        bool IsHistoricalFulfillment);
}
