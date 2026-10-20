using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// Atomic, idempotent POS sale posting (QB-09). One database transaction covers the sale, its immutable
/// line snapshots, stock movements and balances, the confirmed payment, the balanced journal entry, the
/// audit event and the idempotency receipt: a failed sub-step leaves nothing behind (F-01/F-05).
/// </summary>
public static class SaleEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string SaleSequenceName = "sale";
    private const string SaleSequencePrefix = "S-";

    public static IEndpointRouteBuilder MapSaleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pos/sales").WithTags("POS");

        group.MapPost("/", PostAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}/receipt", ReceiptAsync).RequireAuthorization(Permissions.SalesPost);

        return app;
    }

    private static async Task<IResult> PostAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        SaleCaptureRequest request,
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

        // Stable replay: the stored receipt answers with the original sale, never a second business effect.
        var existing = await db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(sale => sale.BusinessId == businessId && sale.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different payload.",
                    $"Key '{idempotencyKey}' already posted sale {existing.SaleNumber}; start a new capture instead of changing the payload.");
            }

            return Results.Ok(ToResult(existing));
        }

        if (request.Payment is null)
        {
            return ApiResults.ValidationProblem(
                "The sale payload is invalid.",
                new Dictionary<string, string[]> { ["payment"] = ["A confirmed payment is required for a posted sale."] });
        }

        var lines = request.Lines ?? [];

        if (SalePosting.ValidatePayload(request, lines) is { Count: > 0 } payloadErrors)
        {
            return ApiResults.ValidationProblem("The sale payload is invalid.", payloadErrors);
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var currency = business.BaseCurrency;

        if (!string.IsNullOrWhiteSpace(request.Currency)
            && !string.Equals(request.Currency.Trim(), currency, StringComparison.OrdinalIgnoreCase))
        {
            return ApiResults.ValidationProblem(
                "The sale currency is invalid.",
                new Dictionary<string, string[]> { ["currency"] = [$"This business sells in {currency} (spec Â§3.5)."] });
        }

        var context = await SalePosting.ResolveContextAsync(db, businessId, request, lines, cancellationToken).ConfigureAwait(false);

        if (context.Error is not null)
        {
            return context.Error;
        }

        var pricing = SalePosting.PriceLines(lines, context.Variants, context.Products, context.TaxRates);

        if (pricing.Error is not null)
        {
            return pricing.Error;
        }

        var payment = SalePosting.ValidatePayment(request, pricing);

        if (payment.Error is not null)
        {
            return payment.Error;
        }

        var register = await SalePosting.ResolveRegisterAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);

        if (register.Error is not null)
        {
            return register.Error;
        }

        try
        {
            var sale = await SalePosting.PostSaleAsync(
                db,
                business,
                userId,
                request,
                pricing.Lines,
                payment,
                register.Session,
                idempotencyKey,
                payloadHash,
                http.TraceIdentifier,
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/pos/sales/{sale.Id}", ToResult(sale));
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

        var sale = await db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return NotFound();
        }

        return Results.Ok(await ToDetailAsync(db, sale, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Receipt content for a posted sale (QB-10). Values come from the sale snapshots, so a later product
    /// rename or price change never alters a receipt; <c>?format=html</c> returns a printable page.
    /// </summary>
    private static async Task<IResult> ReceiptAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        string? format,
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
            return NotFound();
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var receipt = await BuildReceiptAsync(db, business, sale, cancellationToken).ConfigureAwait(false);

        if (string.Equals(format?.Trim(), "html", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Content(ReceiptRenderer.RenderHtml(receipt), "text/html", System.Text.Encoding.UTF8);
        }

        return Results.Ok(receipt);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? locationId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.Sales.AsNoTracking().Where(sale => sale.BusinessId == businessId);

        if (from is { } start)
        {
            query = query.Where(sale => sale.PostedAtUtc >= start);
        }

        if (to is { } end)
        {
            query = query.Where(sale => sale.PostedAtUtc <= end);
        }

        if (locationId is { } location)
        {
            query = query.Where(sale => sale.LocationId == location);
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(sale => sale.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(sale => sale.Id)
            .Take(size + 1)
            .Select(sale => new SaleSummaryRow(
                sale.Id,
                sale.SaleNumber,
                sale.Status,
                sale.LocationId,
                sale.CustomerId,
                sale.Currency,
                sale.TotalAmount,
                sale.TaxAmount,
                sale.PaymentStatus,
                sale.PostedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var page = rows
            .Select(row => new SaleCaptureResult(
                row.Id,
                row.SaleNumber,
                row.Currency,
                row.TotalAmount,
                row.TaxAmount,
                row.PostedAtUtc,
                Provisional: false))
            .ToList();

        return Results.Ok(new PagedResult<SaleCaptureResult>(page, nextCursor, size));
    }


    // ---------------------------------------------------------------- reads

    private static SaleCaptureResult ToResult(Sale sale)
        => new(sale.Id, sale.SaleNumber, sale.Currency, sale.TotalAmount, sale.TaxAmount, sale.PostedAtUtc, Provisional: false);

    private static async Task<PostedSaleDto> ToDetailAsync(BillingDbContext db, Sale sale, CancellationToken cancellationToken)
    {
        var lines = await db.SaleLines.AsNoTracking()
            .Where(line => line.SaleId == sale.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new PostedSaleLineDto(
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
                SalePosting.EffectiveTaxMode(line),
                line.UnitCostSnapshot))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.SaleId == sale.Id)
            .OrderBy(payment => payment.ReceivedAtUtc)
            .Select(payment => new
            {
                payment.Id,
                payment.Method,
                payment.Status,
                payment.Amount,
                payment.Currency,
                payment.CashReceived,
                payment.Reference,
                payment.PspReference,
                payment.ReceivedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PostedSaleDto(
            sale.Id,
            sale.SaleNumber,
            EnumBridge.To<SaleState>(sale.Status),
            sale.LocationId,
            sale.CustomerId,
            sale.Currency,
            sale.SubtotalAmount,
            sale.DiscountAmount,
            sale.TaxAmount,
            sale.TotalAmount,
            EnumBridge.To<QtsBilling.Contracts.Sales.PaymentStatus>(sale.PaymentStatus),
            Provisional: false,
            sale.Note,
            sale.PostedAtUtc,
            sale.PostedByUserId,
            sale.ReversalOfSaleId,
            lines,
            payments.Select(payment => new PostedPaymentDto(
                payment.Id,
                payment.Method.ToString(),
                EnumBridge.To<QtsBilling.Contracts.Sales.PaymentStatus>(payment.Status),
                payment.Amount,
                payment.Currency,
                payment.CashReceived,
                payment.Reference,
                payment.PspReference,
                payment.ReceivedAtUtc)).ToList());
    }

    /// <summary>Receipt content from immutable snapshots; renaming a product or changing a price never alters it.</summary>
    private static async Task<SaleReceiptDto> BuildReceiptAsync(
        BillingDbContext db,
        Business business,
        Sale sale,
        CancellationToken cancellationToken)
    {
        var location = await db.BusinessLocations.AsNoTracking()
            .Where(candidate => candidate.Id == sale.LocationId)
            .Select(candidate => new { candidate.Name, candidate.Address })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        string? cashierName = null;

        if (sale.PostedByUserId is { } cashierId)
        {
            cashierName = await db.Users.AsNoTracking()
                .Where(user => user.Id == cashierId)
                .Select(user => user.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var lines = await db.SaleLines.AsNoTracking()
            .Where(line => line.SaleId == sale.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new SaleReceiptLineDto(
                line.LineNo,
                line.ProductNameSnapshot,
                line.SkuSnapshot,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.TaxRate,
                line.TaxAmount,
                line.LineTotal,
                SalePosting.EffectiveTaxMode(line)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var paymentRows = await db.Payments.AsNoTracking()
            .Where(payment => payment.SaleId == sale.Id)
            .OrderBy(payment => payment.ReceivedAtUtc)
            .Select(payment => new
            {
                payment.Method,
                payment.Status,
                payment.Amount,
                payment.CashReceived,
                payment.Reference,
                payment.CashRegisterSessionId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? registerName = null;
        Guid? sessionId = null;

        if (paymentRows.FirstOrDefault(row => row.CashRegisterSessionId is not null) is { } linked)
        {
            sessionId = linked.CashRegisterSessionId;

            registerName = await db.CashRegisterSessions.AsNoTracking()
                .Where(session => session.Id == linked.CashRegisterSessionId!.Value)
                .Join(
                    db.CashRegisters.AsNoTracking(),
                    session => session.CashRegisterId,
                    register => register.Id,
                    (session, register) => register.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var payments = paymentRows
            .Select(row => new SaleReceiptPaymentDto(
                row.Method.ToString(),
                EnumBridge.To<QtsBilling.Contracts.Sales.PaymentStatus>(row.Status).ToString(),
                row.Amount,
                row.CashReceived,
                row.Method == PaymentMethod.Cash && row.CashReceived is { } received ? LedgerWriter.Round(received - row.Amount) : null,
                row.Reference))
            .ToList();

        return new SaleReceiptDto(
            sale.Id,
            sale.SaleNumber,
            sale.Status == SaleStatus.Reversed ? "Reversed" : "Posted",
            null,
            business.Name,
            business.LegalName,
            location?.Name,
            location?.Address,
            sale.Currency,
            sale.PostedAtUtc,
            FormatLocal(business.TimeZone, sale.PostedAtUtc),
            cashierName,
            registerName,
            sessionId,
            lines,
            sale.SubtotalAmount,
            sale.DiscountAmount,
            sale.TaxAmount,
            sale.TotalAmount,
            payments);
    }

    private static string FormatLocal(string? timeZone, DateTimeOffset instant)
    {
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone))
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    }

    private static IResult NotFound()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The sale does not exist in this business.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_sales_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same key committed first.",
                    "Retry the same payload: the stored sale will be returned instead of posting twice."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The sale write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.InsufficientStock,
                    "The sale violates inventory or amount integrity guards."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The sale references a record outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The sale posting failed."),
        };

    // ---------------------------------------------------------------- local shapes


    private sealed record SaleSummaryRow(
        Guid Id,
        string SaleNumber,
        SaleStatus Status,
        Guid LocationId,
        Guid? CustomerId,
        string Currency,
        decimal TotalAmount,
        decimal TaxAmount,
        QtsBilling.Persistence.Postgres.PaymentStatus PaymentStatus,
        DateTimeOffset PostedAtUtc);
}