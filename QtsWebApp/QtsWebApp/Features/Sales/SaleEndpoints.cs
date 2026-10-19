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
    private const int MaxLines = 200;
    private const int MaxNoteLength = 2_000;
    private const int MaxReferenceLength = 120;
    private const int MoneyScale = 4;
    private const int QuantityScale = 6;

    /// <summary>
    /// Chart-of-accounts codes the sale posting needs (cash, bank, receivables, inventory, tax payable,
    /// revenue, COGS). A business without them cannot post sales — mapping remains configurable in QB-27.
    /// </summary>
    private const string CashAccountCode = "1000";
    private const string BankAccountCode = "1010";
    private const string ReceivableAccountCode = "1100";
    private const string InventoryAccountCode = "1200";
    private const string TaxAccountCode = "2100";
    private const string RevenueAccountCode = "4000";
    private const string CogsAccountCode = "5000";

    public static IEndpointRouteBuilder MapSaleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pos/sales").WithTags("POS");

        group.MapPost("/", PostAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.SalesPost);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.SalesPost);

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

        if (ValidatePayload(request, lines) is { Count: > 0 } payloadErrors)
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
                new Dictionary<string, string[]> { ["currency"] = [$"This business sells in {currency} (spec §3.5)."] });
        }

        var context = await ResolveContextAsync(db, businessId, request, lines, cancellationToken).ConfigureAwait(false);

        if (context.Error is not null)
        {
            return context.Error;
        }

        var pricing = PriceLines(lines, context.Variants, context.Products, context.TaxRates);

        if (pricing.Error is not null)
        {
            return pricing.Error;
        }

        var payment = ValidatePayment(request, pricing);

        if (payment.Error is not null)
        {
            return payment.Error;
        }

        try
        {
            var sale = await PostSaleAsync(
                db,
                business,
                userId,
                request,
                pricing.Lines,
                payment,
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

    // ---------------------------------------------------------------- payload validation

    private static Dictionary<string, string[]> ValidatePayload(SaleCaptureRequest request, IReadOnlyList<SaleLineRequest> lines)
    {
        var errors = new Dictionary<string, string[]>();

        if (lines.Count is 0 or > MaxLines)
        {
            errors["lines"] = [$"A sale requires 1-{MaxLines} lines."];
            return errors;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.Quantity <= 0)
            {
                errors[$"lines[{index}].quantity"] = ["Quantity must be greater than zero."];
            }
            else if (decimal.Round(line.Quantity, QuantityScale) != line.Quantity)
            {
                errors[$"lines[{index}].quantity"] = [$"Quantities support at most {QuantityScale} decimal places."];
            }

            if (line.UnitPrice < 0 || decimal.Round(line.UnitPrice, MoneyScale) != line.UnitPrice)
            {
                errors[$"lines[{index}].unitPrice"] = [$"A unit price of at most {MoneyScale} decimal places and not negative is required."];
            }

            if (line.DiscountAmount < 0 || decimal.Round(line.DiscountAmount, MoneyScale) != line.DiscountAmount)
            {
                errors[$"lines[{index}].discountAmount"] = ["A discount of at most 4 decimal places and not negative is required."];
            }

            if (line.TaxRate < 0 || line.TaxRate > 1)
            {
                errors[$"lines[{index}].taxRate"] = ["Tax rates are fractions between 0 and 1 (assumption recorded in decisions.md D-08)."];
            }
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            errors["note"] = [$"A note of at most {MaxNoteLength} characters is supported."];
        }

        return errors;
    }

    /// <summary>Loads location, customer, variants, products and tax rates the payload references, all scoped to the business.</summary>
    private static async Task<SaleContext> ResolveContextAsync(
        BillingDbContext db,
        Guid businessId,
        SaleCaptureRequest request,
        IReadOnlyList<SaleLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var location = await db.BusinessLocations.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == request.LocationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null || !location.IsActive)
        {
            return new SaleContext(
                ApiResults.ValidationProblem(
                    "The sale payload is invalid.",
                    new Dictionary<string, string[]> { ["locationId"] = ["The location does not exist or is inactive in this business."] }),
                [],
                [],
                new Dictionary<Guid, decimal>());
        }

        if (request.CustomerId is { } customerId)
        {
            var customerExists = await db.Customers.AsNoTracking()
                .AnyAsync(customer => customer.BusinessId == businessId && customer.Id == customerId, cancellationToken)
                .ConfigureAwait(false);

            if (!customerExists)
            {
                return new SaleContext(
                    ApiResults.ValidationProblem(
                        "The sale payload is invalid.",
                        new Dictionary<string, string[]> { ["customerId"] = ["The customer does not exist in this business."] }),
                    [],
                    [],
                    new Dictionary<Guid, decimal>());
            }
        }

        var variantIds = lines.Select(line => line.VariantId).Distinct().ToArray();

        var variants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.BusinessId == businessId && variantIds.Contains(variant.Id))
            .Select(variant => new VariantRow(variant.Id, variant.ProductId, variant.Name, variant.Sku, variant.SellingPrice, variant.PurchasePrice, variant.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var productIds = variants.Select(variant => variant.ProductId).Distinct().ToArray();

        var products = await db.Products.AsNoTracking()
            .Where(product => product.BusinessId == businessId && productIds.Contains(product.Id))
            .Select(product => new ProductRow(product.Id, product.Name, product.IsActive, product.TrackStock, product.TaxType, product.TaxRateId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var taxRateIds = products.Where(product => product.TaxRateId is not null).Select(product => product.TaxRateId!.Value).Distinct().ToArray();

        var taxRates = await db.TaxRates.AsNoTracking()
            .Where(rate => rate.BusinessId == businessId && taxRateIds.Contains(rate.Id))
            .ToDictionaryAsync(rate => rate.Id, rate => rate.Rate, cancellationToken)
            .ConfigureAwait(false);

        return new SaleContext(null, variants, products, taxRates);
    }

    /// <summary>Server-authoritative pricing: the catalog price wins, the stored tax snapshot is used, and a mismatch is rejected.</summary>
    private static PricingResult PriceLines(
        IReadOnlyList<SaleLineRequest> lines,
        IReadOnlyList<VariantRow> variants,
        IReadOnlyList<ProductRow> products,
        IReadOnlyDictionary<Guid, decimal> taxRates)
    {
        var computed = new List<ComputedLine>(lines.Count);
        var errors = new Dictionary<string, string[]>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var variant = variants.FirstOrDefault(candidate => candidate.Id == line.VariantId);

            if (variant is null)
            {
                errors[$"lines[{index}].variantId"] = ["The variant does not exist in this business."];
                continue;
            }

            var product = products.FirstOrDefault(candidate => candidate.Id == variant.ProductId);

            if (product is null || !variant.IsActive || !product.IsActive)
            {
                errors[$"lines[{index}].variantId"] = ["The product or variant is inactive and cannot be sold."];
                continue;
            }

            var serverPrice = LedgerWriter.Round(variant.SellingPrice);

            if (LedgerWriter.Round(line.UnitPrice) != serverPrice)
            {
                return new PricingResult(
                    ApiResults.Problem(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.InvalidPrice,
                        "The submitted price does not match the catalog.",
                        $"Line {index + 1}: submitted {LedgerWriter.Round(line.UnitPrice)}, catalog {serverPrice}. Price overrides require an approved policy (OPEN in decisions.md)."),
                    []);
            }

            var serverRate = product.TaxRateId is { } taxRateId && taxRates.TryGetValue(taxRateId, out var rate) ? rate : 0m;

            if (decimal.Round(line.TaxRate, QuantityScale) != decimal.Round(serverRate, QuantityScale))
            {
                errors[$"lines[{index}].taxRate"] = [$"The stored tax rate for this product is {serverRate}; the server never trusts a client rate."];
                continue;
            }

            var quantity = line.Quantity;
            var gross = LedgerWriter.Round(quantity * serverPrice);
            var discount = LedgerWriter.Round(line.DiscountAmount);

            if (discount > gross)
            {
                errors[$"lines[{index}].discountAmount"] = ["The discount cannot exceed the line gross amount."];
                continue;
            }

            var net = gross - discount;
            var tax = product.TaxType == TaxType.Inclusive
                ? LedgerWriter.Round(net * serverRate / (1 + serverRate))
                : LedgerWriter.Round(net * serverRate);
            var lineTotal = product.TaxType == TaxType.Inclusive ? net : net + tax;

            computed.Add(new ComputedLine(
                index + 1,
                variant.Id,
                variant.Name,
                variant.Sku,
                quantity,
                serverPrice,
                discount,
                serverRate,
                tax,
                lineTotal,
                gross,
                product.TrackStock,
                LedgerWriter.Round(variant.PurchasePrice ?? 0m)));
        }

        if (errors.Count > 0)
        {
            return new PricingResult(ApiResults.ValidationProblem("The sale payload is invalid.", errors), []);
        }

        return new PricingResult(null, computed);
    }

    private static PaymentResult ValidatePayment(SaleCaptureRequest request, PricingResult pricing)
    {
        var payment = request.Payment!;
        var methodName = payment.Method?.Trim() ?? string.Empty;

        if (!Enum.TryParse<PaymentMethod>(methodName, ignoreCase: true, out var method))
        {
            return new PaymentResult(null!, ApiResults.ValidationProblem(
                "The sale payload is invalid.",
                new Dictionary<string, string[]> { ["payment.method"] = [$"Unsupported payment method '{methodName}'."] }));
        }

        var total = pricing.Lines.Sum(line => line.LineTotal);
        var amount = LedgerWriter.Round(payment.Amount);

        if (total <= 0)
        {
            return new PaymentResult(null!, ApiResults.ValidationProblem(
                "The sale payload is invalid.",
                new Dictionary<string, string[]> { ["lines"] = ["A posted sale must have a positive total."] }));
        }

        if (amount != total)
        {
            return new PaymentResult(null!, ApiResults.ValidationProblem(
                "The sale payload is invalid.",
                new Dictionary<string, string[]> { ["payment.amount"] = [$"The payment must equal the sale total {total}; partial or credit sales use their own workflow."] }));
        }

        var reference = payment.Reference?.Trim();

        if (reference is { Length: > MaxReferenceLength })
        {
            return new PaymentResult(null!, ApiResults.ValidationProblem(
                "The sale payload is invalid.",
                new Dictionary<string, string[]> { ["payment.reference"] = [$"A reference of at most {MaxReferenceLength} characters is supported."] }));
        }

        decimal? cashReceived = null;

        switch (method)
        {
            case PaymentMethod.Cash:
                cashReceived = payment.CashReceived is { } received ? LedgerWriter.Round(received) : amount;

                if (cashReceived < amount)
                {
                    return new PaymentResult(null!, ApiResults.ValidationProblem(
                        "The sale payload is invalid.",
                        new Dictionary<string, string[]> { ["payment.cashReceived"] = [$"Cash received cannot be less than the amount due {amount}."] }));
                }

                break;

            case PaymentMethod.CustomerCredit:
                if (request.CustomerId is null)
                {
                    return new PaymentResult(null!, ApiResults.ValidationProblem(
                        "The sale payload is invalid.",
                        new Dictionary<string, string[]> { ["customerId"] = ["A customer is required for an on-account (credit) sale."] }));
                }

                break;

            default:
                // Card, cheque, bank transfer and other externally processed methods need proof of processing:
                // no placeholder may ever report success (F-09, spec §3.6).
                if (string.IsNullOrWhiteSpace(reference))
                {
                    return new PaymentResult(null!, ApiResults.Problem(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.PaymentUnconfirmed,
                        "The payment is not confirmed.",
                        $"{method} payments require the terminal/PSP reference that confirms the money was taken."));
                }

                break;
        }

        return new PaymentResult(
            new ComputedPayment(method, amount, cashReceived, string.IsNullOrWhiteSpace(reference) ? null : reference),
            null);
    }

    // ---------------------------------------------------------------- the atomic unit of work

    private static async Task<Sale> PostSaleAsync(
        BillingDbContext db,
        Business business,
        Guid userId,
        SaleCaptureRequest request,
        IReadOnlyList<ComputedLine> lines,
        PaymentResult payment,
        string idempotencyKey,
        string payloadHash,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry must not see state tracked by an aborted attempt.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var accounts = await LedgerWriter.ResolveAccountsAsync(
                db,
                business.Id,
                [CashAccountCode, BankAccountCode, ReceivableAccountCode, InventoryAccountCode, TaxAccountCode, RevenueAccountCode, CogsAccountCode],
                cancellationToken).ConfigureAwait(false);

            var expectedCodes = new (string Code, string Purpose)[]
            {
                (CashAccountCode, "cash on hand"),
                (BankAccountCode, "bank"),
                (ReceivableAccountCode, "accounts receivable"),
                (InventoryAccountCode, "inventory"),
                (TaxAccountCode, "tax payable"),
                (RevenueAccountCode, "sales revenue"),
                (CogsAccountCode, "cost of goods sold"),
            };

            var missing = expectedCodes
                .Where(entry => !accounts.TryGetValue(entry.Code, out var account) || !account.IsActive)
                .Select(entry => $"{entry.Code} ({entry.Purpose})")
                .ToArray();

            if (missing.Length > 0)
            {
                throw new DomainRuleException(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The chart of accounts is not ready for sale posting.",
                    $"This business must define active accounts: {string.Join(", ", missing)}.");
            }

            var now = DateTimeOffset.UtcNow;
            var entryDate = LedgerWriter.LocalDate(business.TimeZone, now);
            var saleNumber = await LedgerWriter.AllocateNumberAsync(
                db,
                business.Id,
                SaleSequenceName,
                SaleSequencePrefix,
                entryDate.Year,
                cancellationToken).ConfigureAwait(false);

            var subtotal = lines.Sum(line => line.Gross);
            var discount = lines.Sum(line => line.Discount);
            var tax = lines.Sum(line => line.Tax);
            var total = lines.Sum(line => line.LineTotal);
            var cost = lines.Where(line => line.TrackStock).Sum(line => LedgerWriter.Round(line.UnitCost * line.Quantity));

            var sale = new Sale
            {
                BusinessId = business.Id,
                LocationId = request.LocationId,
                CustomerId = request.CustomerId,
                SaleNumber = saleNumber,
                Status = SaleStatus.Posted,
                Currency = business.BaseCurrency,
                SubtotalAmount = subtotal,
                DiscountAmount = discount,
                TaxAmount = tax,
                TotalAmount = total,
                PaymentStatus = QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                PostedAtUtc = now,
                PostedByUserId = userId,
                IdempotencyKey = idempotencyKey,
                PayloadHash = payloadHash,
            };

            db.Sales.Add(sale);

            // Deterministic lock order (business, location, variant): concurrent terminals cannot deadlock.
            foreach (var line in lines.OrderBy(line => line.VariantId))
            {
                var saleLine = new SaleLine
                {
                    BusinessId = business.Id,
                    SaleId = sale.Id,
                    LineNo = line.LineNo,
                    VariantId = line.VariantId,
                    ProductNameSnapshot = line.ProductName,
                    SkuSnapshot = line.Sku,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    DiscountAmount = line.Discount,
                    TaxRate = line.TaxRate,
                    TaxAmount = line.Tax,
                    LineTotal = line.LineTotal,
                    UnitCostSnapshot = line.UnitCost,
                };

                db.SaleLines.Add(saleLine);

                if (line.TrackStock)
                {
                    await InventoryLedger.ApplyAsync(
                        db,
                        business.Id,
                        userId,
                        request.LocationId,
                        line.VariantId,
                        MovementType.Sale,
                        -line.Quantity,
                        line.UnitCost,
                        "sale_line",
                        saleLine.Id,
                        $"Sale {saleNumber}",
                        now,
                        allowNegative: false,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var paymentEntity = new Payment
            {
                BusinessId = business.Id,
                SaleId = sale.Id,
                Method = payment.Payment!.Method,
                Status = QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed,
                Amount = payment.Payment.Amount,
                Currency = business.BaseCurrency,
                CashReceived = payment.Payment.CashReceived,
                Reference = payment.Payment.Reference,
                ReceivedAtUtc = now,
                CreatedByUserId = userId,
            };

            db.Payments.Add(paymentEntity);

            var revenue = subtotal - discount;
            var journalLines = new List<QtsBilling.Contracts.Accounting.JournalLineRequest>
            {
                new(DebitAccountFor(payment.Payment.Method, accounts), total, 0, $"Sale {saleNumber}", request.LocationId),
            };

            if (revenue > 0)
            {
                journalLines.Add(new(accounts[RevenueAccountCode].Id, 0, revenue, "Sales revenue", null));
            }

            if (tax > 0)
            {
                journalLines.Add(new(accounts[TaxAccountCode].Id, 0, tax, "Tax payable", null));
            }

            if (cost > 0)
            {
                journalLines.Add(new(accounts[CogsAccountCode].Id, cost, 0, "Cost of goods sold", null));
                journalLines.Add(new(accounts[InventoryAccountCode].Id, 0, cost, "Inventory relief", null));
            }

            var journal = await LedgerWriter.PostWithinTransactionAsync(
                db,
                business.Id,
                userId,
                business.BaseCurrency,
                entryDate,
                $"Sale {saleNumber}",
                JournalSourceType.Sale,
                sale.Id,
                null,
                journalLines,
                cancellationToken).ConfigureAwait(false);

            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = business.Id,
                UserId = userId,
                EntityType = "sale",
                EntityId = sale.Id,
                Action = "posted",
                OccurredAtUtc = now,
                CorrelationId = correlationId,
                Summary = JsonSerializer.Serialize(new
                {
                    saleNumber,
                    total = total.ToString(CultureInfo.InvariantCulture),
                    tax = tax.ToString(CultureInfo.InvariantCulture),
                    lines = lines.Count,
                    payment = payment.Payment.Method.ToString(),
                    journalEntry = journal.EntryNumber,
                    locationId = request.LocationId,
                }),
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return sale;
        }).ConfigureAwait(false);
    }

    private static Guid DebitAccountFor(PaymentMethod method, IReadOnlyDictionary<string, Account> accounts)
        => method switch
        {
            PaymentMethod.Cash => accounts[CashAccountCode].Id,
            PaymentMethod.CustomerCredit => accounts[ReceivableAccountCode].Id,
            _ => accounts[BankAccountCode].Id,
        };

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

    private sealed record VariantRow(Guid Id, Guid ProductId, string Name, string? Sku, decimal SellingPrice, decimal? PurchasePrice, bool IsActive);

    private sealed record ProductRow(Guid Id, string Name, bool IsActive, bool TrackStock, TaxType TaxType, Guid? TaxRateId);

    private sealed record ComputedLine(
        int LineNo,
        Guid VariantId,
        string ProductName,
        string? Sku,
        decimal Quantity,
        decimal UnitPrice,
        decimal Discount,
        decimal TaxRate,
        decimal Tax,
        decimal LineTotal,
        decimal Gross,
        bool TrackStock,
        decimal UnitCost);

    private sealed record ComputedPayment(PaymentMethod Method, decimal Amount, decimal? CashReceived, string? Reference);

    private sealed record SaleContext(IResult? Error, IReadOnlyList<VariantRow> Variants, IReadOnlyList<ProductRow> Products, IReadOnlyDictionary<Guid, decimal> TaxRates);

    private sealed record PricingResult(IResult? Error, IReadOnlyList<ComputedLine> Lines);

    private sealed record PaymentResult(ComputedPayment Payment, IResult? Error);

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
