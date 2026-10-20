using System.Globalization;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Sales;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsModels.ValueObjects;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Features.Inventory;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Sales;

/// <summary>
/// The sale posting path of QB-09, extracted so that the live POS endpoint and the offline sync handler
/// post a sale with exactly the same rules and exactly the same transaction. The accounting and the
/// inventory rules are never re-implemented for the offline path (specification §3.6/QB-20).
/// </summary>
internal static class SalePosting
{
    private const int MaxLines = 200;
    private const int MaxNoteLength = 2_000;
    private const int MaxReferenceLength = 120;
    private const int MoneyScale = 4;
    private const int QuantityScale = 6;

    private const string SaleSequenceName = "sale";
    private const string SaleSequencePrefix = "S-";

    /// <summary>Chart-of-accounts codes the sale posting needs (cash, bank, receivables, inventory, tax payable, revenue, COGS).</summary>
    private const string CashAccountCode = "1000";
    private const string BankAccountCode = "1010";
    private const string ReceivableAccountCode = "1100";
    private const string InventoryAccountCode = "1200";
    private const string TaxAccountCode = "2100";
    private const string RevenueAccountCode = "4000";
    private const string CogsAccountCode = "5000";
    // ---------------------------------------------------------------- payload validation

    public static Dictionary<string, string[]> ValidatePayload(SaleCaptureRequest request, IReadOnlyList<SaleLineRequest> lines)
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
    public static async Task<SaleContext> ResolveContextAsync(
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
    public static PricingResult PriceLines(
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

            // One shared commercial calculation (specification D03): the server, the browser and the
            // device compute a line with the same function, so the same commercial inputs always give
            // the same amounts. For an inclusive line the tax is already inside the payable, so the
            // revenue base is the payable minus that tax.
            CommercialLineAmounts amounts;

            try
            {
                amounts = CommercialAmounts.ComputeLine(
                    new CommercialLineInput
                    {
                        Quantity = quantity,
                        UnitPrice = serverPrice,
                        TaxMode = product.TaxType == TaxType.Inclusive
                            ? CommercialTaxMode.Inclusive
                            : CommercialTaxMode.Exclusive,
                        TaxRate = serverRate,
                        DiscountAmount = discount,
                        PricingPolicyVersion = line.PricingPolicyVersion,
                    },
                    $"lines[{index}]");
            }
            catch (CommercialAmountException exception)
            {
                errors[exception.Field] = [exception.Message];
                continue;
            }

            computed.Add(new ComputedLine(
                index + 1,
                variant.Id,
                variant.Name,
                variant.Sku,
                quantity,
                serverPrice,
                amounts.DiscountAmount,
                serverRate,
                amounts.TaxAmount,
                amounts.PayableAmount,
                amounts.GrossAmount,
                amounts.RevenueBaseAmount,
                amounts.TaxMode.ToString(),
                product.TrackStock,
                LedgerWriter.Round(variant.PurchasePrice ?? 0m)));
        }

        if (errors.Count > 0)
        {
            return new PricingResult(ApiResults.ValidationProblem("The sale payload is invalid.", errors), []);
        }

        return new PricingResult(null, computed);
    }

    public static PaymentResult ValidatePayment(SaleCaptureRequest request, PricingResult pricing)
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

            case PaymentMethod.Card:
                // No verified PSP integration exists (OPEN-6): a card payment can never be substantiated from
                // this system, so the method is disabled instead of reporting a success it cannot prove (F-09).
                return new PaymentResult(null!, ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.PaymentMethodDisabled,
                    "Card payments are disabled.",
                    "No verified PSP integration exists (OPEN-6). Accept cash, or cheque/bank transfer with a processing reference."));

            default:
                // Cheque, bank transfer and other externally processed methods need proof of processing:
                // no placeholder may ever report success (F-09, spec Â§3.6).
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

    public static async Task<Sale> PostSaleAsync(
        BillingDbContext db,
        Business business,
        Guid userId,
        SaleCaptureRequest request,
        IReadOnlyList<ComputedLine> lines,
        PaymentResult payment,
        RegisterSession? registerSession,
        string idempotencyKey,
        string payloadHash,
        string correlationId,
        CancellationToken cancellationToken,
        ProcessedOperation? processedOperation = null,
        Action<Sale>? beforeCommit = null)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
            // A retry must not see state tracked by an aborted attempt.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            CashRegisterSession? lockedSession = null;

            if (registerSession is { } targetSession)
            {
                // Lock the drawer session and re-verify it is still open: a concurrent close must not be
                // missed between validation and the write (QB-10).
                lockedSession = await db.CashRegisterSessions
                    .FromSql($"SELECT * FROM cash_register_sessions WHERE id = {targetSession.SessionId} AND business_id = {business.Id} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (lockedSession is null || lockedSession.Status != CashRegisterSessionStatus.Open)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.CashRegisterNotOpen,
                        "The drawer session was closed.",
                        "Open a new session before capturing payments on this register.");
                }
            }

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

            // A customer-credit sale is financed, not collected: it creates a receivable and never a
            // confirmed payment. The money has not been taken, so it is not reported as taken
            // (specification D07, finding F14).
            var financedSale = payment.Payment!.Method == PaymentMethod.CustomerCredit;

            // Only cash is settled by its receipt. A cheque or a transfer is not bank money until an
            // operator confirms the processing, so it stays unsettled (specification D07).
            var settledByReceipt = payment.Payment.Method == PaymentMethod.Cash;

            var subtotal = lines.Sum(line => line.Gross);
            var discount = lines.Sum(line => line.Discount);
            var tax = lines.Sum(line => line.Tax);
            var total = lines.Sum(line => line.LineTotal);
            if (request.CustomerId is { } customerId && payment.Payment!.Method != PaymentMethod.Cash)
            {
                await CustomerCreditLimitGuard.EnsureWithinLimitAsync(
                    db,
                    business.Id,
                    customerId,
                    business.BaseCurrency,
                    total,
                    cancellationToken).ConfigureAwait(false);
            }

            // The cost of goods sold is the value the stock movements consume. It is collected while the
            // movements are applied, because only the movement knows the value that was consumed
            // (specification D06, finding F15).
            var cost = 0m;

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
                PaymentStatus = settledByReceipt
                    ? QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed
                    : QtsBilling.Persistence.Postgres.PaymentStatus.Pending,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                PostedAtUtc = now,
                PostedByUserId = userId,
                IdempotencyKey = idempotencyKey,
                PayloadHash = payloadHash,
            };

            db.Sales.Add(sale);

            // The offline sync handler records the committed outcome in the same transaction (QB-20).
            beforeCommit?.Invoke(sale);

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
                    TaxMode = line.TaxMode,
                    UnitCostSnapshot = line.UnitCost,
                };

                db.SaleLines.Add(saleLine);

                if (line.TrackStock)
                {
                    var movement = await InventoryLedger.ApplyAsync(
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
                        cancellationToken).ConfigureAwait(false);

                    // The cost of goods sold is the value the movement actually consumed, never the
                    // catalog price (specification D06, finding F15).
                    cost -= movement.ValueDelta;
                }
            }

            var paymentEntity = new Payment
            {
                BusinessId = business.Id,
                SaleId = sale.Id,
                Method = payment.Payment!.Method,
                // An unsettled tender is never reported as collected: only an acknowledged receipt
                // confirms the money (specification D07).
                Status = settledByReceipt
                    ? QtsBilling.Persistence.Postgres.PaymentStatus.Confirmed
                    : QtsBilling.Persistence.Postgres.PaymentStatus.Pending,
                Amount = payment.Payment.Amount,
                Currency = business.BaseCurrency,
                CashReceived = payment.Payment.CashReceived,
                Reference = payment.Payment.Reference,
                ReceivedAtUtc = now,
                CreatedByUserId = userId,
                CashRegisterSessionId = lockedSession?.Id,
            };

            db.Payments.Add(paymentEntity);

            if (payment.Payment.Method == PaymentMethod.Cash && lockedSession is not null)
            {
                db.CashRegisterMovements.Add(new CashRegisterMovement
                {
                    BusinessId = business.Id,
                    CashRegisterId = lockedSession.CashRegisterId,
                    CashRegisterSessionId = lockedSession.Id,
                    MovementType = CashMovementType.SalePayment,
                    Amount = payment.Payment.Amount,
                    Currency = business.BaseCurrency,
                    SaleId = sale.Id,
                    CreatedByUserId = userId,
                    OccurredAtUtc = now,
                });
            }

            // The revenue is the net base of the lines, never the gross: for a tax-inclusive line the tax
            // is already inside the payable, so crediting the gross would count that tax twice and the
            // entry would not balance (specification D03, finding F11).
            var revenue = total - tax;
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
                    registerId = lockedSession?.CashRegisterId,
                    cashRegisterSessionId = lockedSession?.Id,
                }),
            });

            if (processedOperation is not null)
            {
                db.ProcessedOperations.Add(processedOperation);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return sale;
            }).ConfigureAwait(false);
        }
        finally
        {
            BillingMetrics.RecordPosSalePostingDuration(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    /// <summary>
    /// The effective tax composition of a posted line. The recorded snapshot wins. A line that was
    /// written before the composition was recorded is classified from its own numbers - the quantity,
    /// the price, the discount, the rate and the tax amount it was posted with - and never from the
    /// current catalog, which may have changed since the document was written. A line whose numbers
    /// match neither composition stays unclassified instead of being guessed.
    /// </summary>
    public static string EffectiveTaxMode(SaleLine line)
    {
        if (!string.IsNullOrWhiteSpace(line.TaxMode)
            && !string.Equals(line.TaxMode, "Unspecified", StringComparison.OrdinalIgnoreCase))
        {
            return line.TaxMode;
        }

        var derived = CommercialAmounts.ClassifyStoredLine(
            line.Quantity,
            line.UnitPrice,
            line.DiscountAmount,
            line.TaxRate,
            line.TaxAmount);

        return derived?.ToString() ?? "Unspecified";
    }

    public static Guid DebitAccountFor(PaymentMethod method, IReadOnlyDictionary<string, Account> accounts)
        => method switch
        {
            PaymentMethod.Cash => accounts[CashAccountCode].Id,

            // A cheque or a transfer is not bank money until an operator confirms the processing, so
            // the amount stays on the receivable until the collection is recorded (specification D07).
            PaymentMethod.Cheque or PaymentMethod.BankTransfer or PaymentMethod.Other
                or PaymentMethod.CustomerCredit => accounts[ReceivableAccountCode].Id,

            _ => accounts[BankAccountCode].Id,
        };

    /// <summary>
    /// Resolves the optional drawer session: a register must be active, belong to the sale location and have an
    /// open session, otherwise a cashier would believe the money is accounted when it is not (QB-10).
    /// </summary>
    public static async Task<RegisterContext> ResolveRegisterAsync(
        BillingDbContext db,
        Guid businessId,
        SaleCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RegisterId is not { } registerId)
        {
            return new RegisterContext(null, null);
        }

        var register = await db.CashRegisters.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == registerId, cancellationToken)
            .ConfigureAwait(false);

        if (register is null || !register.IsActive || register.LocationId != request.LocationId)
        {
            return new RegisterContext(
                ApiResults.ValidationProblem(
                    "The sale payload is invalid.",
                    new Dictionary<string, string[]> { ["registerId"] = ["The register must be active and belong to the sale location (spec Â§3.6)."] }),
                null);
        }

        var session = await db.CashRegisterSessions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId
                && candidate.CashRegisterId == registerId
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
                    "Open a drawer session before capturing payments on this register."),
                null);
        }

        return new RegisterContext(null, new RegisterSession(register.Id, session.Id));
    }

    internal sealed record VariantRow(Guid Id, Guid ProductId, string Name, string? Sku, decimal SellingPrice, decimal? PurchasePrice, bool IsActive);

    internal sealed record ProductRow(Guid Id, string Name, bool IsActive, bool TrackStock, TaxType TaxType, Guid? TaxRateId);

    internal sealed record ComputedLine(
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
        /// <summary>The net revenue of the line: the payable minus the tax that is already inside it.</summary>
        decimal RevenueBase,
        /// <summary>How the tax was composed into the price (Exclusive, Inclusive or Exempt).</summary>
        string TaxMode,
        bool TrackStock,
        decimal UnitCost);

    internal sealed record ComputedPayment(PaymentMethod Method, decimal Amount, decimal? CashReceived, string? Reference);

    internal sealed record SaleContext(IResult? Error, IReadOnlyList<VariantRow> Variants, IReadOnlyList<ProductRow> Products, IReadOnlyDictionary<Guid, decimal> TaxRates);

    internal sealed record RegisterSession(Guid RegisterId, Guid SessionId);

    internal sealed record RegisterContext(IResult? Error, RegisterSession? Session);

    internal sealed record PricingResult(IResult? Error, IReadOnlyList<ComputedLine> Lines);

    internal sealed record PaymentResult(ComputedPayment Payment, IResult? Error);
}
