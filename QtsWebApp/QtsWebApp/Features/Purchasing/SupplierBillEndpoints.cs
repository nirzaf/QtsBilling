using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pos;
using QtsBilling.Contracts.Purchasing;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsModels.ValueObjects;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Purchasing;

/// <summary>Supplier bill matching, AP open items and allocated supplier settlements.</summary>
public static class SupplierBillEndpoints
{
    private const int MaxBillLines = 100;
    private const int MaxCurrencyLength = 3;
    private const string IdempotencyHeader = "Idempotency-Key";

    // The control accounts the payables side recognises through. These are the same codes the bill itself posts to,
    // so a credit reverses exactly what the bill recognised rather than inventing a second mapping.
    private const string PayableAccountCode = "2000";
    private const string GrniAccountCode = "2101";
    private const string InputTaxAccountCode = "1300";
    private const string PriceVarianceAccountCode = "5150";

    private const string CreditSequenceName = "supplier-credit";
    private const string CreditSequencePrefix = "SC-";

    public static IEndpointRouteBuilder MapSupplierBillEndpoints(this IEndpointRouteBuilder app)
    {
        var bills = app.MapGroup("/api/v1/purchasing/bills").WithTags("Purchasing");
        bills.MapGet("/", ListBillsAsync).RequireAuthorization(Permissions.PurchasingManage);
        bills.MapPost("/", CreateBillAsync).RequireAuthorization(Permissions.PurchasingManage);
        bills.MapGet("/{id:guid}", GetBillAsync).RequireAuthorization(Permissions.PurchasingManage);
        bills.MapPost("/{id:guid}/issue", IssueBillAsync).RequireAuthorization(Permissions.AccountingPost);

        // A supplier credit reverses a posted bill, so it belongs to the posting role and not to whoever matched the
        // bill (specification task 27, recipe step 3).
        bills.MapPost("/{id:guid}/credits", CreateCreditAsync).RequireAuthorization(Permissions.AccountingPost);

        var credits = app.MapGroup("/api/v1/purchasing/credits").WithTags("Purchasing");
        credits.MapGet("/{id:guid}", GetCreditAsync).RequireAuthorization(Permissions.PurchasingManage);
        credits.MapPost("/{id:guid}/applications", ApplyCreditAsync).RequireAuthorization(Permissions.AccountingPost);

        app.MapPost("/api/v1/purchasing/supplier-payments", CreateSupplierPaymentAsync)
            .WithTags("Purchasing")
            .RequireAuthorization(Permissions.AccountingPost);

        // The payable account read as at one business date, which is what tells an operator whether a settlement or
        // a credit is right (recipe step 5). Reading an account is a purchasing-management act, the same authority
        // the customer statement gives its reader.
        app.MapGet("/api/v1/suppliers/{id:guid}/statement", SupplierStatementAsync)
            .WithTags("Purchasing")
            .RequireAuthorization(Permissions.PurchasingManage);

        return app;
    }

    private static async Task<IResult> ListBillsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid? supplierId,
        bool? openOnly,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var query = db.SupplierBills.AsNoTracking().Where(bill => bill.BusinessId == businessId);
        if (supplierId is { } supplier)
        {
            query = query.Where(bill => bill.SupplierId == supplier);
        }

        if (openOnly == true)
        {
            query = query.Where(bill => bill.Status == SupplierBillStatus.Issued
                || bill.Status == SupplierBillStatus.PartiallyPaid);
        }

        var rows = await query.OrderByDescending(bill => bill.CreatedAtUtc).ThenByDescending(bill => bill.Id)
            .Take(size).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(bill => bill.Id).ToArray();
        var lines = await db.SupplierBillLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && ids.Contains(line.SupplierBillId))
            .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byBill = lines.GroupBy(line => line.SupplierBillId).ToDictionary(group => group.Key, group => group.ToArray());

        return Results.Ok(await ProjectAsync(db, businessId, rows, byBill, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetBillAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var bill = await db.SupplierBills.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (bill is null)
        {
            return NotFoundBill();
        }

        var lines = await db.SupplierBillLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.SupplierBillId == bill.Id)
            .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);

        var byBill = new Dictionary<Guid, SupplierBillLine[]> { [bill.Id] = [.. lines] };
        return Results.Ok((await ProjectAsync(db, businessId, [bill], byBill, cancellationToken).ConfigureAwait(false))[0]);
    }

    private static async Task<IResult> CreateBillAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CreateSupplierBillRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var key = RequiredIdempotencyKey(http);
        if (key is null)
        {
            return RequiredKeyProblem();
        }

        var errors = await ValidateBillRequestAsync(db, businessId, request, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The supplier bill payload is invalid.", errors);
        }

        var receiptLines = new List<(PurchaseReceiptLine Line, PurchaseReceipt Receipt, PurchaseOrder Order)>();
        var commercialInputs = new List<CommercialLineInput>(request.Lines.Count);
        try
        {
            foreach (var (line, index) in request.Lines.Select((line, index) => (line, index)))
            {
                var receiptLine = await db.PurchaseReceiptLines.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == line.PurchaseReceiptLineId, cancellationToken)
                    .ConfigureAwait(false);
                if (receiptLine is null)
                {
                    errors[$"lines[{index}].purchaseReceiptLineId"] = ["Select a goods receipt line from this business."];
                    continue;
                }

                var receipt = await db.PurchaseReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == receiptLine.PurchaseReceiptId, cancellationToken)
                    .ConfigureAwait(false);
                var order = receipt is null
                    ? null
                    : await db.PurchaseOrders.AsNoTracking()
                        .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == receipt.PurchaseOrderId, cancellationToken)
                        .ConfigureAwait(false);
                if (receipt is null || receipt.Kind != PurchaseReceiptKind.Receipt || order is null)
                {
                    errors[$"lines[{index}].purchaseReceiptLineId"] = ["The goods receipt line is not from a posted supplier receipt."];
                    continue;
                }

                if (order.SupplierId != request.SupplierId || order.LocationId != request.LocationId
                    || (request.PurchaseOrderId is { } requestedOrder && requestedOrder != order.Id))
                {
                    errors[$"lines[{index}].purchaseReceiptLineId"] = ["All matched receipts must belong to the selected supplier, location, and purchase order."];
                    continue;
                }

                if (receiptLines.Count > 0 && (receiptLines[0].Order.Id != order.Id
                    || !string.Equals(receiptLines[0].Order.Currency, order.Currency, StringComparison.OrdinalIgnoreCase)))
                {
                    errors[$"lines[{index}].purchaseReceiptLineId"] = ["A supplier bill must match receipts from one purchase order and currency."];
                    continue;
                }

                receiptLines.Add((receiptLine, receipt, order));
                commercialInputs.Add(new CommercialLineInput
                {
                    Quantity = line.QuantityBilled,
                    UnitPrice = line.UnitCost,
                    DiscountAmount = line.DiscountAmount,
                    TaxRate = line.TaxRate,
                    TaxMode = CommercialAmountCalculator.ParseTaxMode(line.TaxMode, $"lines[{index}].taxMode"),
                });
            }

            if (errors.Count > 0)
            {
                return ApiResults.ValidationProblem("The supplier bill payload is invalid.", errors);
            }
        }
        catch (CommercialAmountException exception)
        {
            return ApiResults.ValidationProblem("The supplier bill amounts are invalid.",
                new Dictionary<string, string[]> { [exception.Field] = [exception.Message] });
        }

        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, request.LocationId, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                "The signed-in user cannot access this location.");
        }

        var execution = await IdempotencyExecutor.ExecuteAsync<CreateSupplierBillRequest, SupplierBillDto>(
            db, businessId, "purchasing.bills.create", key, request,
            async () =>
            {
                var order = receiptLines[0].Order;
                var settings = await LedgerWriter.ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
                var document = CommercialAmountCalculator.ComputeDocument(commercialInputs, settings?.SettlementScale ?? 4);
                var bill = new SupplierBill
                {
                    BusinessId = businessId,
                    SupplierId = order.SupplierId,
                    LocationId = order.LocationId,
                    PurchaseOrderId = order.Id,
                    BillNumber = await LedgerWriter.AllocateNumberAsync(db, businessId, "supplier-bill", "SB-", request.BillDate.Year, cancellationToken).ConfigureAwait(false),
                    SupplierInvoiceReference = string.IsNullOrWhiteSpace(request.SupplierInvoiceReference) ? null : request.SupplierInvoiceReference.Trim(),
                    Status = SupplierBillStatus.Draft,
                    BillDate = request.BillDate,
                    DueDate = request.DueDate,
                    Currency = order.Currency,
                    SubtotalAmount = document.GrossAmount,
                    DiscountAmount = document.DiscountAmount,
                    TaxAmount = document.TaxAmount,
                    RoundingAmount = document.RoundingAmount,
                    TotalAmount = document.SettlementPayableAmount,
                    BalanceDue = document.SettlementPayableAmount,
                };
                db.SupplierBills.Add(bill);

                var billLineRows = new List<SupplierBillLine>(request.Lines.Count);
                for (var index = 0; index < request.Lines.Count; index++)
                {
                    var source = request.Lines[index];
                    var receiptLine = receiptLines[index].Line;
                    var amounts = document.Lines[index];
                    var row = new SupplierBillLine
                    {
                        BusinessId = businessId,
                        SupplierBillId = bill.Id,
                        LineNo = index + 1,
                        PurchaseReceiptLineId = receiptLine.Id,
                        VariantId = receiptLine.VariantId,
                        ProductNameSnapshot = receiptLine.ProductNameSnapshot,
                        QuantityBilled = source.QuantityBilled,
                        UnitCost = source.UnitCost,
                        GrossAmount = amounts.GrossAmount,
                        DiscountAmount = amounts.DiscountAmount,
                        TaxRate = amounts.TaxRate,
                        TaxAmount = amounts.TaxAmount,
                        LineTotal = amounts.PayableAmount,
                        NetAmount = amounts.RevenueBaseAmount,
                        TaxMode = amounts.TaxMode.ToString(),
                    };
                    db.SupplierBillLines.Add(row);
                    billLineRows.Add(row);
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "supplier_bill",
                    EntityId = bill.Id,
                    Action = "created",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        billNumber = bill.BillNumber,
                        supplierId = bill.SupplierId,
                        total = bill.TotalAmount,
                        lineCount = billLineRows.Count,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var createdLines = await db.SupplierBillLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.SupplierBillId == bill.Id)
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                var createdByBill = new Dictionary<Guid, SupplierBillLine[]> { [bill.Id] = [.. createdLines] };

                return (StatusCodes.Status201Created, (await ProjectAsync(db, businessId, [bill], createdByBill, cancellationToken).ConfigureAwait(false))[0]);
            }, cancellationToken).ConfigureAwait(false);

        return Results.Json(execution.Body, statusCode: execution.StatusCode);
    }

    private static async Task<IResult> IssueBillAsync(
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

        var key = RequiredIdempotencyKey(http);
        if (key is null)
        {
            return RequiredKeyProblem();
        }

        var execution = await IdempotencyExecutor.ExecuteAsync<object, SupplierBillDto>(
            db, businessId, "purchasing.bills.issue", key, new { billId = id },
            async () =>
            {
                var bill = await db.SupplierBills
                    .FromSql($"SELECT * FROM supplier_bills WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (bill is null)
                {
                    throw BillNotFoundException();
                }

                if (bill.Status != SupplierBillStatus.Draft)
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The supplier bill is not a draft.", "Only a draft supplier bill can be issued.");
                }

                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, bill.LocationId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                        "The signed-in user cannot access this location.", "Supplier bill posting is restricted to the member's assigned locations.");
                }

                var lines = await db.SupplierBillLines.Where(line => line.BusinessId == businessId && line.SupplierBillId == bill.Id)
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (lines.Count == 0)
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The supplier bill has no matched receipt lines.", "Add at least one goods receipt line before issuing the bill.");
                }

                var receiptLineIds = lines.Select(line => line.PurchaseReceiptLineId).Distinct().Order().ToArray();
                var lockedReceiptLines = await db.PurchaseReceiptLines
                    .FromSql($"SELECT * FROM purchase_receipt_lines WHERE business_id = {businessId} AND id = ANY({receiptLineIds}) ORDER BY id FOR UPDATE")
                    .ToDictionaryAsync(line => line.Id, cancellationToken).ConfigureAwait(false);
                decimal grniAmount = 0m;
                foreach (var line in lines)
                {
                    if (!lockedReceiptLines.TryGetValue(line.PurchaseReceiptLineId, out var receiptLine))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The linked goods receipt line is missing.", "The bill cannot be issued because its receipt evidence is unavailable.");
                    }

                    var receipt = await db.PurchaseReceipts.AsNoTracking()
                        .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == receiptLine.PurchaseReceiptId, cancellationToken)
                        .ConfigureAwait(false);
                    var order = receipt is null ? null : await db.PurchaseOrders.AsNoTracking()
                        .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == receipt.PurchaseOrderId, cancellationToken)
                        .ConfigureAwait(false);
                    if (receipt is null || receipt.Kind != PurchaseReceiptKind.Receipt || order is null
                        || order.SupplierId != bill.SupplierId || order.LocationId != bill.LocationId
                        || order.Id != bill.PurchaseOrderId)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The linked goods receipt does not match this bill.", "Supplier, location, and purchase order must agree across the three-way match.");
                    }

                    var alreadyBilled = await db.SupplierBillLines.AsNoTracking()
                        .Where(otherLine => otherLine.BusinessId == businessId
                            && otherLine.PurchaseReceiptLineId == line.PurchaseReceiptLineId
                            && db.SupplierBills.Any(otherBill => otherBill.BusinessId == businessId
                                && otherBill.Id == otherLine.SupplierBillId
                                && otherBill.Status != SupplierBillStatus.Draft
                                && otherBill.Status != SupplierBillStatus.Void))
                        .SumAsync(otherLine => (decimal?)otherLine.QuantityBilled, cancellationToken)
                        .ConfigureAwait(false) ?? 0m;
                    if (alreadyBilled + line.QuantityBilled > receiptLine.Quantity)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.OverReceipt,
                            "The supplier bill exceeds received quantity.",
                            $"Receipt line {receiptLine.Id} has {receiptLine.Quantity} units; {alreadyBilled} have already been billed and this request adds {line.QuantityBilled}.");
                    }

                    grniAmount += LedgerWriter.Round(line.QuantityBilled * receiptLine.UnitCost);
                }

                if (bill.TotalAmount <= 0m)
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "A zero-value supplier bill cannot be posted.", "Supplier bills must create a positive AP balance.");
                }

                var billedNet = lines.Sum(line => line.NetAmount);
                var priceVariance = billedNet - grniAmount;
                var requiredCodes = new List<string> { "2000", "2101" };
                if (bill.TaxAmount != 0m) requiredCodes.Add("1300");
                if (priceVariance != 0m) requiredCodes.Add("5150");
                if (bill.RoundingAmount != 0m) requiredCodes.Add("5999");
                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, requiredCodes, cancellationToken).ConfigureAwait(false);
                foreach (var code in requiredCodes)
                {
                    if (!accounts.ContainsKey(code))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "Required supplier-billing ledger accounts are not configured.", $"Configure the account mapped to {code} before posting supplier bills.");
                    }
                }

                var journalLines = new List<JournalLineRequest>();
                if (grniAmount != 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["2101"].Id, grniAmount, 0m, "Clear received goods from GRNI", bill.LocationId));
                }
                if (bill.TaxAmount != 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["1300"].Id, bill.TaxAmount, 0m, "Recoverable input VAT", bill.LocationId));
                }
                if (priceVariance > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["5150"].Id, priceVariance, 0m, "Supplier invoice price variance", bill.LocationId));
                }
                else if (priceVariance < 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["5150"].Id, 0m, -priceVariance, "Supplier invoice price variance", bill.LocationId));
                }
                if (bill.RoundingAmount > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["5999"].Id, bill.RoundingAmount, 0m, "Settlement rounding", bill.LocationId));
                }
                else if (bill.RoundingAmount < 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts["5999"].Id, 0m, -bill.RoundingAmount, "Settlement rounding", bill.LocationId));
                }
                journalLines.Add(new JournalLineRequest(accounts["2000"].Id, 0m, bill.TotalAmount, "Supplier accounts payable", bill.LocationId));

                var journal = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, bill.Currency,
                    bill.BillDate, $"Supplier bill {bill.BillNumber}", JournalSourceType.Purchase, bill.Id, null,
                    journalLines, cancellationToken).ConfigureAwait(false);
                bill.JournalEntryId = journal.Id;
                bill.Status = SupplierBillStatus.Issued;
                bill.IssuedAtUtc = DateTimeOffset.UtcNow;
                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "supplier_bill",
                    EntityId = bill.Id,
                    Action = "issued",
                    OccurredAtUtc = bill.IssuedAtUtc.Value,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        billNumber = bill.BillNumber,
                        total = bill.TotalAmount,
                        journalEntryId = journal.Id,
                    }),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var allBillLines = await db.SupplierBillLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.SupplierBillId == bill.Id)
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                var issuedByBill = new Dictionary<Guid, SupplierBillLine[]> { [bill.Id] = [.. allBillLines] };

                return (StatusCodes.Status200OK, (await ProjectAsync(db, businessId, [bill], issuedByBill, cancellationToken).ConfigureAwait(false))[0]);
            }, cancellationToken).ConfigureAwait(false);

        return Results.Json(execution.Body, statusCode: execution.StatusCode);
    }

    private static async Task<IResult> CreateSupplierPaymentAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CreateSupplierPaymentRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var key = RequiredIdempotencyKey(http);
        if (key is null)
        {
            return RequiredKeyProblem();
        }

        var errors = ValidatePaymentRequest(request);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The supplier payment payload is invalid.", errors);
        }

        if (!Enum.TryParse<PaymentMethod>(request.Method, true, out var method)
            || !Enum.IsDefined(method)
            || method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            return ApiResults.ValidationProblem("The supplier payment method is invalid.",
                new Dictionary<string, string[]> { [nameof(request.Method)] = ["Supplier payments currently support Cash and BankTransfer; card and cheque clearing require a verified payment flow."] });
        }

        var execution = await IdempotencyExecutor.ExecuteAsync<CreateSupplierPaymentRequest, SupplierPaymentDto>(
            db, businessId, "purchasing.supplier-payments.create", key, request,
            async () =>
            {
                var targets = request.Allocations
                    .OrderBy(item => TargetKey(item), StringComparer.Ordinal)
                    .ToArray();
                var billIds = targets.Select(item => item.SupplierBillId).OfType<Guid>().ToArray();
                var openItemIds = targets.Select(item => item.OpenItemId).OfType<Guid>().ToArray();

                var bills = billIds.Length == 0
                    ? new Dictionary<Guid, SupplierBill>()
                    : await db.SupplierBills
                        .FromSql($"SELECT * FROM supplier_bills WHERE business_id = {businessId} AND id = ANY({billIds}) ORDER BY id FOR UPDATE")
                        .ToDictionaryAsync(bill => bill.Id, cancellationToken).ConfigureAwait(false);
                var openings = openItemIds.Length == 0
                    ? new Dictionary<Guid, OpenItem>()
                    : await db.OpenItems
                        .FromSql($"SELECT * FROM open_items WHERE business_id = {businessId} AND id = ANY({openItemIds}) ORDER BY id FOR UPDATE")
                        .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);

                // A settlement is only as true as the documents it names: every target must exist here, belong to this
                // supplier and currency, and still owe the amount being applied (specification task 27, recipe step 4).
                foreach (var target in targets)
                {
                    if (target.SupplierBillId is { } billId)
                    {
                        if (!bills.TryGetValue(billId, out var bill)
                            || bill.SupplierId != request.SupplierId
                            || !string.Equals(bill.Currency, request.Currency, StringComparison.OrdinalIgnoreCase)
                            || bill.Status is not (SupplierBillStatus.Issued or SupplierBillStatus.PartiallyPaid)
                            || target.Amount > bill.BalanceDue)
                        {
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                                "A supplier payment allocation is invalid.",
                                "Allocations must match the supplier and currency, target open bills, and not exceed each balance due.");
                        }

                        if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, bill.LocationId, cancellationToken).ConfigureAwait(false))
                        {
                            throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                                "The signed-in user cannot access a location of a bill being settled.",
                                "Supplier payments are restricted to the member's assigned locations.");
                        }

                        bill.AmountPaid += target.Amount;
                        bill.BalanceDue -= target.Amount;
                        bill.Status = bill.BalanceDue == 0m ? SupplierBillStatus.Paid : SupplierBillStatus.PartiallyPaid;
                        continue;
                    }

                    if (target.OpenItemId is not { } openItemId
                        || !openings.TryGetValue(openItemId, out var opening)
                        || opening.Kind != OpenItemKind.Payable
                        || opening.SupplierId != request.SupplierId
                        || !string.Equals(opening.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "A supplier payment allocation is invalid.",
                            "An opening payable must belong to this supplier and currency; no bill is invented to pay it.");
                    }

                    // The imported figure is never patched: what remains is derived from every settlement and credit
                    // already applied to it, so a concurrent pair cannot both spend the last of it (D17).
                    var settledByPayments = await db.SupplierPaymentAllocations.AsNoTracking()
                        .Where(allocation => allocation.BusinessId == businessId && allocation.OpenItemId == opening.Id)
                        .SumAsync(allocation => (decimal?)allocation.AllocatedAmount, cancellationToken).ConfigureAwait(false) ?? 0m;
                    var settledByCredits = await db.SupplierCreditApplications.AsNoTracking()
                        .Where(application => application.BusinessId == businessId && application.OpenItemId == opening.Id)
                        .SumAsync(application => (decimal?)application.Amount, cancellationToken).ConfigureAwait(false) ?? 0m;
                    if (opening.OriginalAmount - settledByPayments - settledByCredits < target.Amount)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The payment exceeds what is left of an opening payable.",
                            $"The opening was imported at {opening.OriginalAmount}; {settledByPayments + settledByCredits} is already settled.");
                    }
                }

                var accountCodes = method == PaymentMethod.Cash ? new[] { PayableAccountCode, "1000" } : new[] { PayableAccountCode, "1010" };
                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, accountCodes, cancellationToken).ConfigureAwait(false);
                if (accountCodes.Any(code => !accounts.ContainsKey(code)))
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "Required supplier-payment ledger accounts are not configured.", "Configure AP and the selected cash or bank account before settling bills.");
                }
                var paymentId = Guid.CreateVersion7();                var journal = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, request.Currency,
                    request.PaidOn, $"Supplier payment {request.Reference}".Trim(), JournalSourceType.Purchase, paymentId, null,
                    [
                        new JournalLineRequest(accounts["2000"].Id, request.Amount, 0m, "Settle supplier accounts payable", null),
                        new JournalLineRequest(accounts[accountCodes[1]].Id, 0m, request.Amount, "Supplier payment", null),
                    ], cancellationToken).ConfigureAwait(false);

                var payment = new SupplierPayment
                {
                    Id = paymentId,
                    BusinessId = businessId,
                    SupplierId = request.SupplierId,
                    Method = method,
                    Amount = request.Amount,
                    Currency = request.Currency,
                    PaidOn = request.PaidOn,
                    Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
                    JournalEntryId = journal.Id,
                };
                db.SupplierPayments.Add(payment);
                foreach (var target in targets)
                {
                    db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
                    {
                        BusinessId = businessId,
                        SupplierPaymentId = payment.Id,
                        SupplierBillId = target.SupplierBillId,
                        OpenItemId = target.OpenItemId,
                        AllocatedAmount = target.Amount,
                    });
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "supplier_payment",
                    EntityId = payment.Id,
                    Action = "created",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        payment.Amount,
                        payment.Method,
                        payment.Currency,
                        allocationCount = targets.Length,
                        journalEntryId = journal.Id,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (StatusCodes.Status201Created, new SupplierPaymentDto(
                    payment.Id, payment.SupplierId, payment.Method.ToString(), payment.Amount, payment.Currency,
                    payment.PaidOn, payment.Reference, payment.JournalEntryId, targets, journal.EntryNumber));
            }, cancellationToken).ConfigureAwait(false);

        return Results.Json(execution.Body, statusCode: execution.StatusCode);
    }

    /// <summary>
    /// A supplier credit against an issued bill (specification task 27, recipe step 3). It reverses the bill's own
    /// figures for the credited quantity, so the price the bill charged and the cost the receipt capitalised are both
    /// honoured and their difference lands in the price-variance account instead of disappearing. A credit that
    /// follows a posted return names it and clears the goods-received-not-invoiced value that return left behind;
    /// a credit never touches stock by itself, which is why a returned delivery cannot be taken out of inventory
    /// twice. The value reduces the bill it came from first, and whatever is left stays as the credit's own explicit
    /// balance rather than a negative payable.
    /// </summary>
    private static async Task<IResult> CreateCreditAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CreateSupplierCreditRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var key = RequiredIdempotencyKey(http);
        if (key is null)
        {
            return RequiredKeyProblem();
        }

        var errors = ValidateCreditRequest(request);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The supplier credit payload is invalid.", errors);
        }

        var execution = await IdempotencyExecutor.ExecuteAsync<CreateSupplierCreditRequest, SupplierBillDto>(
            db, businessId, "purchasing.bills.credits.create", key, request,
            async () =>
            {
                var bill = await db.SupplierBills
                    .FromSql($"SELECT * FROM supplier_bills WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (bill is null)
                {
                    throw BillNotFoundException();
                }

                if (bill.Status is SupplierBillStatus.Draft or SupplierBillStatus.Void)
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.SupplierBillNotIssued,
                        "Only an issued supplier bill can be credited.",
                        $"{bill.BillNumber} is {bill.Status}; a credit reverses an obligation the ledger already carries, so issue the bill first or leave the draft as it is.");
                }

                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, bill.LocationId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                        "The signed-in user cannot access this location.", "Supplier credits are restricted to the member's assigned locations.");
                }

                var billLineIds = request.Lines.Select(line => line.SupplierBillLineId).Distinct().ToArray();
                var billLines = await db.SupplierBillLines
                    .FromSql($"SELECT * FROM supplier_bill_lines WHERE business_id = {businessId} AND id = ANY({billLineIds}) ORDER BY id FOR UPDATE")
                    .ToDictionaryAsync(line => line.Id, cancellationToken).ConfigureAwait(false);
                if (billLines.Count != billLineIds.Length || billLines.Values.Any(line => line.SupplierBillId != bill.Id))
                {
                    throw new DomainRuleException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                        "A credited line does not belong to this supplier bill.",
                        "Every credit line names a bill line of the bill being credited; the bill was not changed.");
                }

                // The bill lines are locked above, so credits for one line serialize here and the sum can never pass
                // the quantity or the value the bill actually charged.
                var priorLines = await db.SupplierCreditNoteLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && billLineIds.Contains(line.SupplierBillLineId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var priorByBillLine = priorLines
                    .GroupBy(line => line.SupplierBillLineId)
                    .ToDictionary(
                        group => group.Key,
                        group => (Quantity: group.Sum(line => line.Quantity),
                            Base: group.Sum(line => line.BaseAmount),
                            Tax: group.Sum(line => line.TaxAmount),
                            Value: group.Sum(line => line.ValueAmount)));

                PurchaseReceipt? source = null;
                if (request.SourcePurchaseReceiptId is { } sourceReceiptId)
                {
                    source = await db.PurchaseReceipts.AsNoTracking()
                        .SingleOrDefaultAsync(receipt => receipt.BusinessId == businessId && receipt.Id == sourceReceiptId, cancellationToken)
                        .ConfigureAwait(false);
                    if (source is null || source.Kind != PurchaseReceiptKind.Return || source.PurchaseOrderId != bill.PurchaseOrderId)
                    {
                        throw new DomainRuleException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                            "The source return is not a posted return of this bill's purchase order.",
                            "A credit that claims returned goods must follow the return document that took them out of this branch's stock.");
                    }
                }

                var sourceReceiptLineIds = source is null
                    ? []
                    : await db.PurchaseReceiptLines.AsNoTracking()
                        .Where(line => line.BusinessId == businessId && line.PurchaseReceiptId == source.Id)
                        .Select(line => line.Id)
                        .ToListAsync(cancellationToken).ConfigureAwait(false);

                foreach (var line in request.Lines)
                {
                    var hasReceiptLine = line.PurchaseReceiptLineId is not null;
                    if (source is null && hasReceiptLine)
                    {
                        throw new DomainRuleException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                            "A credit with no source return cannot name receipt lines.",
                            "Either follow a posted return, or credit the bill's value alone; a goods cost with no document behind it is the double removal this rule prevents.");
                    }

                    if (hasReceiptLine && !sourceReceiptLineIds.Contains(line.PurchaseReceiptLineId!.Value))
                    {
                        throw new DomainRuleException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                            "A named receipt line does not belong to the source return.",
                            "Each credited line's receipt evidence must come from the return the credit cites.");
                    }

                    if (!billLines.TryGetValue(line.SupplierBillLineId, out var billLine))
                    {
                        throw new DomainRuleException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                            "A credited line does not belong to this supplier bill.", "The bill was not changed.");
                    }

                    var alreadyCredited = priorByBillLine.TryGetValue(line.SupplierBillLineId, out var prior)
                        ? prior.Quantity
                        : 0m;
                    if (alreadyCredited + line.Quantity > billLine.QuantityBilled)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.OverCredit,
                            "The credit exceeds what is left of a bill line.",
                            $"Bill line {billLine.LineNo} charged {billLine.QuantityBilled}; {alreadyCredited} is already credited and this request adds {line.Quantity}.");
                    }
                }

                var receiptLineIds = request.Lines.Select(line => line.PurchaseReceiptLineId).OfType<Guid>().Distinct().ToArray();
                var receiptCosts = receiptLineIds.Length == 0
                    ? new Dictionary<Guid, decimal>()
                    : await db.PurchaseReceiptLines.AsNoTracking()
                        .Where(receiptLine => receiptLine.BusinessId == businessId && receiptLineIds.Contains(receiptLine.Id))
                        .ToDictionaryAsync(receiptLine => receiptLine.Id, receiptLine => receiptLine.UnitCost, cancellationToken).ConfigureAwait(false);

                var business = await db.Businesses.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The business of this supplier bill is not available.", "A credit cannot be posted without the business currency and calendar.");

                var creditId = Guid.CreateVersion7();
                var creditLines = new List<SupplierCreditNoteLine>(request.Lines.Count);
                var byBillLine = request.Lines
                    .GroupBy(line => line.SupplierBillLineId)
                    .Select(group => (Line: billLines[group.Key], Requests: group.ToArray()))
                    .ToList();

                foreach (var (billLine, requests) in byBillLine)
                {
                    var alreadyCredited = priorByBillLine.TryGetValue(billLine.Id, out var prior)
                        ? prior
                        : (Quantity: 0m, Base: 0m, Tax: 0m, Value: 0m);
                    var remainingQuantity = billLine.QuantityBilled - alreadyCredited.Quantity;
                    var quantity = requests.Sum(item => item.Quantity);

                    decimal baseAmount;
                    decimal taxAmount;

                    // The last credit for a line takes the exact residual, so a chain of partial credits adds up to
                    // the bill's own figures rather than drifting by a rounding unit (specification T03/F05 rule).
                    if (quantity == remainingQuantity)
                    {
                        baseAmount = billLine.NetAmount - alreadyCredited.Base;
                        taxAmount = billLine.TaxAmount - alreadyCredited.Tax;
                    }
                    else
                    {
                        baseAmount = LedgerWriter.Round(billLine.NetAmount * quantity / billLine.QuantityBilled);
                        taxAmount = LedgerWriter.Round(billLine.TaxAmount * quantity / billLine.QuantityBilled);
                    }

                    var first = true;
                    foreach (var requestLine in requests)
                    {
                        var lineBase = first
                            ? LedgerWriter.Round(billLine.NetAmount * requestLine.Quantity / billLine.QuantityBilled)
                            : baseAmount - creditLines.Where(row => row.SupplierBillLineId == billLine.Id).Sum(row => row.BaseAmount);
                        var lineTax = first
                            ? LedgerWriter.Round(billLine.TaxAmount * requestLine.Quantity / billLine.QuantityBilled)
                            : taxAmount - creditLines.Where(row => row.SupplierBillLineId == billLine.Id && row.PurchaseReceiptLineId is null).Sum(row => row.TaxAmount);
                        first = false;

                        creditLines.Add(new SupplierCreditNoteLine
                        {
                            BusinessId = businessId,
                            SupplierCreditNoteId = creditId,
                            LineNo = creditLines.Count + 1,
                            SupplierBillLineId = billLine.Id,
                            PurchaseReceiptLineId = requestLine.PurchaseReceiptLineId,
                            VariantId = billLine.VariantId,
                            ProductNameSnapshot = billLine.ProductNameSnapshot,
                            Quantity = requestLine.Quantity,
                            UnitCost = billLine.UnitCost,
                            BaseAmount = lineBase,
                            TaxRate = billLine.TaxRate,
                            TaxAmount = lineTax,
                            ValueAmount = lineBase + lineTax,
                        });
                    }
                }

                var creditBase = creditLines.Sum(line => line.BaseAmount);
                var creditTax = creditLines.Sum(line => line.TaxAmount);
                var creditValue = creditBase + creditTax;
                var clearedReceiptCost = creditLines.Sum(line => line.PurchaseReceiptLineId is { } receiptLineId
                    ? LedgerWriter.Round(line.Quantity * receiptCosts.GetValueOrDefault(receiptLineId))
                    : 0m);
                var variance = creditValue - creditTax - clearedReceiptCost;

                if (creditValue <= 0m)
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.OverCredit,
                        "The credit carries no value.", "A supplier credit must reverse a positive amount of the bill.");
                }

                var requiredCodes = new List<string> { PayableAccountCode };
                if (creditTax != 0m) requiredCodes.Add(InputTaxAccountCode);
                if (clearedReceiptCost != 0m) requiredCodes.Add(GrniAccountCode);
                if (variance != 0m) requiredCodes.Add(PriceVarianceAccountCode);
                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, requiredCodes, cancellationToken).ConfigureAwait(false);
                foreach (var code in requiredCodes)
                {
                    if (!accounts.ContainsKey(code))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "Required supplier-credit ledger accounts are not configured.",
                            $"Configure the account mapped to {code} before posting supplier credits.");
                    }
                }

                var journalLines = new List<JournalLineRequest>
                {
                    new(accounts[PayableAccountCode].Id, creditValue, 0m, $"Credit against supplier bill {bill.BillNumber}", bill.LocationId),
                };
                if (creditTax != 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts[InputTaxAccountCode].Id, 0m, creditTax, "Input tax reversed with the credit", bill.LocationId));
                }

                if (clearedReceiptCost != 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts[GrniAccountCode].Id, 0m, clearedReceiptCost, "Clear the return value the goods carried", bill.LocationId));
                }

                if (variance > 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts[PriceVarianceAccountCode].Id, 0m, variance, "Favourable price variance reversed by the credit", bill.LocationId));
                }
                else if (variance < 0m)
                {
                    journalLines.Add(new JournalLineRequest(accounts[PriceVarianceAccountCode].Id, -variance, 0m, "Unfavourable price variance carried by the credit", bill.LocationId));
                }

                var now = DateTimeOffset.UtcNow;
                var creditNumber = await LedgerWriter.AllocateNumberAsync(
                    db, businessId, CreditSequenceName, CreditSequencePrefix, LedgerWriter.LocalDate(business.TimeZone, now).Year, cancellationToken).ConfigureAwait(false);

                var journal = await LedgerWriter.PostWithinTransactionAsync(
                    db, businessId, userId, bill.Currency,
                    LedgerWriter.LocalDate(business.TimeZone, now), $"Supplier credit {creditNumber}",
                    JournalSourceType.Purchase, creditId, reversalOfEntryId: null, journalLines, cancellationToken).ConfigureAwait(false);

                // The credit pays its own bill first; what is left of its value stays as the credit's balance, which
                // the payables screen reads as a stated figure with an account behind it (T27.04).
                var appliedHere = bill.BalanceDue >= creditValue ? creditValue : bill.BalanceDue;
                bill.AmountPaid += appliedHere;
                bill.BalanceDue -= appliedHere;
                bill.Status = bill.BalanceDue == 0m ? SupplierBillStatus.Paid : SupplierBillStatus.PartiallyPaid;

                var credit = new SupplierCreditNote
                {
                    Id = creditId,
                    BusinessId = businessId,
                    SupplierBillId = bill.Id,
                    SupplierId = bill.SupplierId,
                    LocationId = bill.LocationId,
                    SourcePurchaseReceiptId = source?.Id,
                    CreditNumber = creditNumber,
                    Reason = request.Reason.Trim(),
                    Currency = bill.Currency,
                    BaseAmount = creditBase,
                    TaxAmount = creditTax,
                    ValueAmount = creditValue,
                    AppliedAmount = appliedHere,
                    BalanceDue = creditValue - appliedHere,
                    JournalEntryId = journal.Id,
                    IssuedAtUtc = now,
                    IssuedByUserId = userId,
                    IdempotencyKey = key,
                    PayloadHash = RequestFingerprint.Compute(request),
                };
                db.SupplierCreditNotes.Add(credit);
                db.SupplierCreditNoteLines.AddRange(creditLines);

                if (appliedHere > 0m)
                {
                    db.SupplierCreditApplications.Add(new SupplierCreditApplication
                    {
                        BusinessId = businessId,
                        SupplierCreditNoteId = credit.Id,
                        SupplierBillId = bill.Id,
                        OpenItemId = null,
                        Amount = appliedHere,
                        AppliedAtUtc = now,
                        AppliedByUserId = userId,
                    });
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "supplier_credit",
                    EntityId = credit.Id,
                    Action = "issued",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        creditNumber = credit.CreditNumber,
                        billNumber = bill.BillNumber,
                        value = credit.ValueAmount,
                        applied = appliedHere,
                        held = credit.BalanceDue,
                        sourceReceipt = source?.ReceiptNumber,
                        journalEntryId = journal.Id,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var allLines = await db.SupplierBillLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.SupplierBillId == bill.Id)
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                var byBill = new Dictionary<Guid, SupplierBillLine[]> { [bill.Id] = [.. allLines] };

                return (StatusCodes.Status201Created, (await ProjectAsync(db, businessId, [bill], byBill, cancellationToken).ConfigureAwait(false))[0]);
            }, cancellationToken).ConfigureAwait(false);

        return Results.Json(execution.Body, statusCode: execution.StatusCode);
    }

    private static async Task<IResult> GetCreditAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var credit = await db.SupplierCreditNotes.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (credit is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The supplier credit was not found.");
        }

        var bill = await db.SupplierBills.AsNoTracking()
            .SingleAsync(row => row.BusinessId == businessId && row.Id == credit.SupplierBillId, cancellationToken)
            .ConfigureAwait(false);
        var byBill = new Dictionary<Guid, SupplierBillLine[]> { [bill.Id] = [.. await db.SupplierBillLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && line.SupplierBillId == bill.Id)
            .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false)] };

        // The bill read is the credit read: it carries the credits, their journal and the remaining eligibility of
        // every line, so one projection answers both without a second, drifting shape.
        return Results.Ok((await ProjectAsync(db, businessId, [bill], byBill, cancellationToken).ConfigureAwait(false))[0]);
    }

    /// <summary>
    /// Use of a credit's remaining value against another open bill or an imported opening payable. No journal is
    /// posted here - the credit already reduced the payable when it was issued - so applying it twice is impossible
    /// by construction, and an opening's imported amount stays untouched while its derived balance falls (D17).
    /// </summary>
    private static async Task<IResult> ApplyCreditAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        ApplySupplierCreditRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var key = RequiredIdempotencyKey(http);
        if (key is null)
        {
            return RequiredKeyProblem();
        }

        if (request.SupplierBillId is null == request.OpenItemId is null
            || request.Amount <= 0m || CommercialAmounts.HasMoreDecimals(request.Amount, 10_000))
        {
            return ApiResults.ValidationProblem("The credit application is invalid.", new Dictionary<string, string[]>
            {
                [nameof(request.SupplierBillId)] = ["Name exactly one target - an open bill or an opening payable - with a positive amount of at most four decimal places."],
            });
        }

        var execution = await IdempotencyExecutor.ExecuteAsync<ApplySupplierCreditRequest, SupplierBillDto>(
            db, businessId, "purchasing.credits.apply", key, request,
            async () =>
            {
                var credit = await db.SupplierCreditNotes
                    .FromSql($"SELECT * FROM supplier_credit_notes WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (credit is null)
                {
                    throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound,
                        "The supplier credit was not found.", "A credit is applied in the business that issued it.");
                }

                if (credit.BalanceDue < request.Amount)
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The credit does not hold that much.",
                        $"{credit.CreditNumber} has {credit.BalanceDue} of {credit.Currency} unused; this request names {request.Amount}.");
                }

                if (!await BusinessScope.CanAccessLocationAsync(db, userId, businessId, credit.LocationId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainRuleException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden,
                        "The signed-in user cannot access this location.", "Supplier credits are applied within the member's assigned locations.");
                }

                var now = DateTimeOffset.UtcNow;
                SupplierBill? target = null;
                OpenItem? opening = null;

                if (request.SupplierBillId is { } billId)
                {
                    target = await db.SupplierBills
                        .FromSql($"SELECT * FROM supplier_bills WHERE business_id = {businessId} AND id = {billId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    if (target is null || target.SupplierId != credit.SupplierId
                        || !string.Equals(target.Currency, credit.Currency, StringComparison.OrdinalIgnoreCase)
                        || target.Status is not (SupplierBillStatus.Issued or SupplierBillStatus.PartiallyPaid)
                        || target.BalanceDue < request.Amount)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The credit cannot be applied to that bill.",
                            "A credit settles an open bill of the same supplier and currency, up to the balance that bill still shows.");
                    }

                    target.AmountPaid += request.Amount;
                    target.BalanceDue -= request.Amount;
                    target.Status = target.BalanceDue == 0m ? SupplierBillStatus.Paid : SupplierBillStatus.PartiallyPaid;
                }
                else
                {
                    opening = await db.OpenItems
                        .FromSql($"SELECT * FROM open_items WHERE business_id = {businessId} AND id = {request.OpenItemId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    if (opening is null || opening.Kind != OpenItemKind.Payable || opening.SupplierId != credit.SupplierId
                        || !string.Equals(opening.Currency, credit.Currency, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The credit cannot be applied to that opening payable.",
                            "An opening must be this supplier's payable in the same currency; its imported amount is never rewritten.");
                    }

                    var settled = await db.SupplierPaymentAllocations.AsNoTracking()
                        .Where(allocation => allocation.BusinessId == businessId && allocation.OpenItemId == opening.Id)
                        .SumAsync(allocation => (decimal?)allocation.AllocatedAmount, cancellationToken).ConfigureAwait(false) ?? 0m;
                    var used = await db.SupplierCreditApplications.AsNoTracking()
                        .Where(application => application.BusinessId == businessId && application.OpenItemId == opening.Id)
                        .SumAsync(application => (decimal?)application.Amount, cancellationToken).ConfigureAwait(false) ?? 0m;
                    if (opening.OriginalAmount - settled - used < request.Amount)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "The credit exceeds what is left of that opening payable.",
                            $"The opening was imported at {opening.OriginalAmount}; {settled + used} is already settled.");
                    }
                }

                credit.AppliedAmount += request.Amount;
                credit.BalanceDue -= request.Amount;

                db.SupplierCreditApplications.Add(new SupplierCreditApplication
                {
                    BusinessId = businessId,
                    SupplierCreditNoteId = credit.Id,
                    SupplierBillId = target?.Id,
                    OpenItemId = opening?.Id,
                    Amount = request.Amount,
                    AppliedAtUtc = now,
                    AppliedByUserId = userId,
                });

                var settledBill = target ?? await db.SupplierBills.AsNoTracking()
                    .SingleAsync(row => row.BusinessId == businessId && row.Id == credit.SupplierBillId, cancellationToken)
                    .ConfigureAwait(false);

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "supplier_credit",
                    EntityId = credit.Id,
                    Action = "applied",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        creditNumber = credit.CreditNumber,
                        amount = request.Amount,
                        target = target?.BillNumber ?? (opening is null ? "unknown" : $"opening {opening.Reference}"),
                        remaining = credit.BalanceDue,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var lines = await db.SupplierBillLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.SupplierBillId == settledBill.Id)
                    .OrderBy(line => line.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
                var byBill = new Dictionary<Guid, SupplierBillLine[]> { [settledBill.Id] = [.. lines] };

                return (StatusCodes.Status200OK, (await ProjectAsync(db, businessId, [settledBill], byBill, cancellationToken).ConfigureAwait(false))[0]);
            }, cancellationToken).ConfigureAwait(false);

        return Results.Json(execution.Body, statusCode: execution.StatusCode);
    }

    /// <summary>
    /// One supplier's account as at a business date (specification task 27, recipe step 5): the documents still owed
    /// - issued bills and imported opening payables together - what has settled them, the credits held and their
    /// unused balance stated apart from the payable, and the control-account figures beside them. A statement is a
    /// read, so it honours the reader's branch scope exactly as the bill list does: a document from a branch this
    /// membership cannot act in is not disclosed by a balance.
    /// </summary>
    private static async Task<IResult> SupplierStatementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var readerId))
        {
            return BusinessScope.MissingMembership();
        }

        var supplier = await db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (supplier is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound,
                "The supplier does not exist in this business.");
        }

        var business = await db.Businesses.AsNoTracking()
            .SingleAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
        var settings = await LedgerWriter.ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        var allowedLocations = await BusinessScope.AccessibleLocationIdsAsync(db, readerId, businessId, cancellationToken)
            .ConfigureAwait(false);
        var date = asOf ?? LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);

        // One clock, the business's own. The default is computed in the business time zone, so it has to be checked
        // in that same time zone - comparing it against the UTC calendar made this route refuse its own default for
        // the hours after 21:00 UTC in a zone ahead of Greenwich (specification F21, found by the task 27 read).
        var businessToday = LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);
        if (date > businessToday)
        {
            return ApiResults.ValidationProblem("The statement date cannot be in the future.", new Dictionary<string, string[]>
            {
                [nameof(asOf)] = [$"Choose {businessToday:yyyy-MM-dd} or an earlier business date."],
            });
        }

        var bills = await db.SupplierBills.AsNoTracking()
            .Where(bill => bill.BusinessId == businessId && bill.SupplierId == id
                && bill.Status != SupplierBillStatus.Draft && bill.BillDate <= date
                && (allowedLocations == null || allowedLocations.Contains(bill.LocationId)))
            .OrderBy(bill => bill.BillDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        var billIds = bills.Select(bill => bill.Id).ToArray();
        var openings = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId && item.Kind == OpenItemKind.Payable
                && item.SupplierId == id && item.DocumentDate <= date)
            .OrderBy(item => item.DocumentDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        var openItemIds = openings.Select(item => item.Id).ToArray();

        var payments = await db.SupplierPayments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId && payment.SupplierId == id && payment.PaidOn <= date)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var paymentIds = payments.Select(payment => payment.Id).ToArray();
        var paymentAllocations = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.BusinessId == businessId && paymentIds.Contains(allocation.SupplierPaymentId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var credits = await db.SupplierCreditNotes.AsNoTracking()
            .Where(credit => credit.BusinessId == businessId && credit.SupplierId == id
                && billIds.Contains(credit.SupplierBillId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The credit's business date is derived from the business's own zone, which SQL cannot evaluate - so the
        // rows are read and then filtered here rather than with a predicate the translator would fail on.
        credits = credits
            .Where(credit => LedgerWriter.LocalDate(business.TimeZone, credit.IssuedAtUtc) <= date)
            .ToList();
        var creditIds = credits.Select(credit => credit.Id).ToArray();
        var creditApplications = await db.SupplierCreditApplications.AsNoTracking()
            .Where(application => application.BusinessId == businessId
                && creditIds.Contains(application.SupplierCreditNoteId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        creditApplications = creditApplications
            .Where(application => LedgerWriter.LocalDate(business.TimeZone, application.AppliedAtUtc) <= date)
            .ToList();

        var settledByBill = paymentAllocations
            .Where(allocation => allocation.SupplierBillId is not null)
            .GroupBy(allocation => allocation.SupplierBillId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(allocation => allocation.AllocatedAmount));
        var creditedByBill = creditApplications
            .Where(application => application.SupplierBillId is not null)
            .GroupBy(application => application.SupplierBillId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(application => application.Amount));
        var settledByOpening = paymentAllocations
            .Where(allocation => allocation.OpenItemId is not null)
            .GroupBy(allocation => allocation.OpenItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(allocation => allocation.AllocatedAmount));
        var creditedByOpening = creditApplications
            .Where(application => application.OpenItemId is not null)
            .GroupBy(application => application.OpenItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(application => application.Amount));

        var events = new List<(string Kind, Guid Id, string Reference, DateOnly DocumentDate, DateOnly? DueDate,
            decimal Debit, decimal Credit, string? Note)>();

        foreach (var opening in openings)
        {
            events.Add(("opening", opening.Id, opening.Reference ?? "OPENING", opening.DocumentDate, opening.DueDate,
                Debit: 0m, Credit: opening.OriginalAmount, Note: "Imported opening payable"));
        }

        foreach (var bill in bills)
        {
            events.Add(("bill", bill.Id, bill.BillNumber, bill.BillDate, bill.DueDate, Debit: 0m, Credit: bill.TotalAmount,
                Note: bill.SupplierInvoiceReference is { Length: > 0 } reference
                    ? $"Supplier invoice {reference}"
                    : null));
        }

        foreach (var payment in payments)
        {
            events.Add(("payment", payment.Id, payment.Reference ?? payment.Method.ToString(), payment.PaidOn, payment.PaidOn,
                Debit: payment.Amount, Credit: 0m, Note: $"Settled by {payment.Method}"));
        }

        foreach (var credit in credits)
        {
            // The credit left the payable when it was issued, so the statement carries it then - not when a later
            // application decides which document absorbed it.
            events.Add(("credit", credit.Id, credit.CreditNumber, LedgerWriter.LocalDate(business.TimeZone, credit.IssuedAtUtc), null,
                Debit: credit.ValueAmount, Credit: 0m, Note: credit.Reason));
        }

        const int window = 400;
        var ordered = events
            .OrderBy(row => row.DocumentDate)
            .ThenBy(row => row.Credit > 0m ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToList();
        var truncated = ordered.Count > window;
        var shown = truncated ? ordered[^window..] : ordered;
        var carried = LedgerWriter.Round(ordered.Sum(row => row.Credit - row.Debit)
            - shown.Sum(row => row.Credit - row.Debit));

        var running = carried;
        var lines = new List<SupplierStatementLineDto>(shown.Count);
        foreach (var row in shown)
        {
            running = LedgerWriter.Round(running + row.Credit - row.Debit);
            lines.Add(new SupplierStatementLineDto(row.Kind, row.Id, row.Reference, row.DocumentDate, row.DueDate,
                row.Debit, row.Credit, running, row.Note));
        }

        var openItems = new List<SupplierOpenItemDto>();
        foreach (var bill in bills)
        {
            var settled = LedgerWriter.Round(
                settledByBill.GetValueOrDefault(bill.Id) + creditedByBill.GetValueOrDefault(bill.Id));
            var remaining = bill.TotalAmount - settled;
            if (remaining > 0m)
            {
                openItems.Add(new SupplierOpenItemDto("bill", bill.Id, bill.BillNumber, bill.BillDate, bill.DueDate,
                    bill.TotalAmount, settled, LedgerWriter.Round(remaining), bill.Currency,
                    bill.DueDate is { } due && due < date, bill.SupplierInvoiceReference));
            }
        }

        foreach (var opening in openings)
        {
            var settled = LedgerWriter.Round(
                settledByOpening.GetValueOrDefault(opening.Id) + creditedByOpening.GetValueOrDefault(opening.Id));
            var remaining = opening.OriginalAmount - settled;
            if (remaining > 0m)
            {
                openItems.Add(new SupplierOpenItemDto("opening", opening.Id, opening.Reference ?? "OPENING",
                    opening.DocumentDate, opening.DueDate, opening.OriginalAmount, settled, LedgerWriter.Round(remaining),
                    opening.Currency, opening.DueDate is { } due && due < date, "Imported opening payable"));
            }
        }

        var heldCredits = LedgerWriter.Round(credits.Sum(credit => credit.BalanceDue));

        // The control accounts read straight from the ledger, so the statement can be tied out rather than trusted:
        // what the payable account carries must equal the documents owed (the OpenItems above) less the credits held
        // on account. That identity is what task 27's reconciliation cases assert against the ledger.
        var controlBalances = await ControlBalancesAsync(db, businessId, date, cancellationToken).ConfigureAwait(false);
        var creditsByBill = await LoadCreditsAsync(db, businessId, billIds, new Dictionary<Guid, SupplierBillLine>(), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new SupplierStatementDto(
            id,
            supplier.Name,
            business.BaseCurrency,
            date,
            DateTimeOffset.UtcNow,
            OpeningBalance: LedgerWriter.Round(openings.Sum(item => item.OriginalAmount)),
            MovementDebit: LedgerWriter.Round(ordered.Sum(row => row.Debit)),
            MovementCredit: LedgerWriter.Round(ordered.Sum(row => row.Credit)),
            ClosingBalance: running,
            UnappliedCredits: heldCredits,
            PayableControlBalance: controlBalances.GetValueOrDefault(PayableAccountCode),
            GrniBalance: controlBalances.GetValueOrDefault(GrniAccountCode),
            OpenDocumentCount: openItems.Count,
            Truncated: truncated,
            Lines: lines,
            OpenItems: openItems,
            Credits: [.. creditsByBill.Values.SelectMany(list => list)],
            SettlementScale: settings?.SettlementScale,
            TimeZoneId: business.TimeZone));
    }

    /// <summary>Credit-minus-debit balances of the payables control accounts as at one business date.</summary>
    private static async Task<Dictionary<string, decimal>> ControlBalancesAsync(
        BillingDbContext db,
        Guid businessId,
        DateOnly asOf,
        CancellationToken cancellationToken)
    {
        var codes = new[] { PayableAccountCode, GrniAccountCode, InputTaxAccountCode, PriceVarianceAccountCode };
        var rows = await (from line in db.JournalLines.AsNoTracking()
                          join entry in db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                          join account in db.Accounts.AsNoTracking() on line.AccountId equals account.Id
                          where line.BusinessId == businessId
                              && entry.BusinessId == businessId
                              && account.BusinessId == businessId
                              && entry.EntryDate <= asOf
                              && codes.Contains(account.Code)
                          select new { account.Code, line.DebitAmount, line.CreditAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.GroupBy(row => row.Code).ToDictionary(
            group => group.Key,
            group => LedgerWriter.Round(group.Sum(row => row.CreditAmount - row.DebitAmount)));
    }

    private static Dictionary<string, string[]> ValidateCreditRequest(CreateSupplierCreditRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors[nameof(request.Reason)] = ["A supplier credit must say why: the reason is audited with who issued it."];
        }
        else if (request.Reason.Length > 512)
        {
            errors[nameof(request.Reason)] = ["The reason may contain at most 512 characters."];
        }

        if (request.Lines is null || request.Lines.Count is 0 or > MaxBillLines)
        {
            errors[nameof(request.Lines)] = [$"A supplier credit requires 1-{MaxBillLines} bill lines."];
            return errors;
        }

        if (request.Lines.Any(line => line.SupplierBillLineId == Guid.Empty)
            || request.Lines.Select(line => (line.SupplierBillLineId, line.PurchaseReceiptLineId)).Distinct().Count() != request.Lines.Count)
        {
            errors[nameof(request.Lines)] = ["Every credited bill line must be specified, and the same line may not be credited twice in one document."];
        }

        foreach (var (line, index) in request.Lines.Select((line, index) => (line, index)))
        {
            if (line.Quantity <= 0m || CommercialAmounts.HasMoreDecimals(line.Quantity, 1_000_000))
            {
                errors[$"lines[{index}].quantity"] = ["A credited quantity must be positive and carry at most six decimal places."];
            }
        }

        return errors;
    }

    private static async Task<Dictionary<string, string[]>> ValidateBillRequestAsync(
        BillingDbContext db,
        Guid businessId,
        CreateSupplierBillRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SupplierId == Guid.Empty) errors[nameof(request.SupplierId)] = ["A supplier is required."];
        if (request.LocationId == Guid.Empty) errors[nameof(request.LocationId)] = ["A location is required."];
        if (request.BillDate == default) errors[nameof(request.BillDate)] = ["A valid supplier bill date is required."];
        if (request.DueDate is { } due && due < request.BillDate) errors[nameof(request.DueDate)] = ["The due date cannot precede the bill date."];
        if (request.SupplierInvoiceReference is { Length: > 128 })
        {
            errors[nameof(request.SupplierInvoiceReference)] = ["Supplier invoice references may contain at most 128 characters."];
        }
        else if (!string.IsNullOrWhiteSpace(request.SupplierInvoiceReference)
            && await db.SupplierBills.AsNoTracking().AnyAsync(bill => bill.BusinessId == businessId
                && bill.SupplierId == request.SupplierId
                && bill.SupplierInvoiceReference == request.SupplierInvoiceReference.Trim()
                && bill.Status != SupplierBillStatus.Void, cancellationToken).ConfigureAwait(false))
        {
            // The supplier's own number identifies the obligation, so the second bill carrying it is refused here -
            // before any amount is computed - rather than relying on the unique index to fail an insert.
            errors[nameof(request.SupplierInvoiceReference)] = [
                $"{request.SupplierInvoiceReference.Trim()} is already on file for this supplier. If the supplier restated the same invoice, credit the existing bill instead of entering it twice."];
        }
        if (request.Lines is null || request.Lines.Count is 0 or > MaxBillLines)
        {
            errors[nameof(request.Lines)] = [$"A supplier bill requires 1-{MaxBillLines} receipt-matched lines."];
            return errors;
        }

        if (request.Lines.Any(line => line.PurchaseReceiptLineId == Guid.Empty)
            || request.Lines.Select(line => line.PurchaseReceiptLineId).Distinct().Count() != request.Lines.Count)
        {
            errors[nameof(request.Lines)] = ["Every receipt line must be specified once; duplicate receipt-line matches are not allowed."];
        }

        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];
            try
            {
                _ = CommercialAmountCalculator.ComputeLine(line.QuantityBilled, line.UnitCost,
                    CommercialAmountCalculator.ParseTaxMode(line.TaxMode, $"lines[{index}].taxMode"),
                    line.TaxRate, line.DiscountAmount, field: $"lines[{index}]");
            }
            catch (CommercialAmountException exception)
            {
                errors[exception.Field] = [exception.Message];
            }
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidatePaymentRequest(CreateSupplierPaymentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SupplierId == Guid.Empty) errors[nameof(request.SupplierId)] = ["A supplier is required."];
        if (request.Amount <= 0m || CommercialAmounts.HasMoreDecimals(request.Amount, 10_000))
            errors[nameof(request.Amount)] = ["The payment must be positive and carry at most four decimal places."];
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != MaxCurrencyLength)
            errors[nameof(request.Currency)] = ["Use a three-character ISO currency code."];
        if (request.PaidOn == default) errors[nameof(request.PaidOn)] = ["A valid payment date is required."];
        if (request.Reference is { Length: > 128 }) errors[nameof(request.Reference)] = ["Payment references may contain at most 128 characters."];
        if (request.Allocations is null || request.Allocations.Count is 0 or > MaxBillLines)
        {
            errors[nameof(request.Allocations)] = [$"A supplier payment requires 1-{MaxBillLines} target allocations."];
            return errors;
        }

        // Exactly one target per allocation (specification task 27, recipe step 2): the same rule the database enforces,
        // answered here so the operator is told which target is ambiguous instead of meeting a constraint violation.
        if (request.Allocations.Any(item => item.SupplierBillId is null == item.OpenItemId is null
                || item.Amount <= 0m || CommercialAmounts.HasMoreDecimals(item.Amount, 10_000))
            || request.Allocations.Select(TargetKey).Distinct().Count() != request.Allocations.Count)
        {
            errors[nameof(request.Allocations)] = ["Every allocation must name exactly one target - a bill or an opening payable - once each, with positive amounts of at most four decimal places."];
        }
        else if (request.Allocations.Sum(item => item.Amount) != request.Amount)
        {
            errors[nameof(request.Allocations)] = ["The payment amount must exactly equal the sum of its allocations."];
        }

        return errors;
    }

    private static string TargetKey(SupplierAllocationTarget target)
        => target.SupplierBillId is { } bill ? $"bill:{bill:D}" : $"opening:{target.OpenItemId:D}";

    private static SupplierBillDto ToDto(SupplierBill bill, IReadOnlyList<SupplierBillLine> lines)
        => new(bill.Id, bill.BillNumber, EnumBridge.To<SupplierBillState>(bill.Status), bill.SupplierId,
            bill.LocationId, bill.PurchaseOrderId, bill.SupplierInvoiceReference, bill.BillDate, bill.DueDate,
            bill.Currency, bill.SubtotalAmount, bill.DiscountAmount, bill.TaxAmount, bill.RoundingAmount,
            bill.TotalAmount, bill.AmountPaid, bill.BalanceDue, bill.JournalEntryId,
            lines.Select(line => new SupplierBillLineDto(line.Id, line.LineNo, line.PurchaseReceiptLineId,
                line.VariantId, line.ProductNameSnapshot, line.QuantityBilled, line.UnitCost, line.GrossAmount, line.DiscountAmount,
                line.TaxRate, line.TaxMode, line.NetAmount, line.TaxAmount, line.LineTotal)).ToArray());

    /// <summary>
    /// The bill as an operator reads it (specification task 27): the supplier, the branch and the order are named
    /// rather than shown as keys, the entry that carries the obligation is numbered, and every credit already taken
    /// against each line is stated with the credits themselves, so what remains creditable is visible before anyone
    /// types a quantity (R07, and recipe step 3's cumulative guard made readable).
    /// </summary>
    private static async Task<SupplierBillDto[]> ProjectAsync(
        BillingDbContext db,
        Guid businessId,
        IReadOnlyList<SupplierBill> bills,
        IReadOnlyDictionary<Guid, SupplierBillLine[]> linesByBill,
        CancellationToken cancellationToken)
    {
        if (bills.Count == 0)
        {
            return [];
        }

        var billIds = bills.Select(bill => bill.Id).ToArray();
        var supplierIds = bills.Select(bill => bill.SupplierId).Distinct().ToArray();
        var locationIds = bills.Select(bill => bill.LocationId).Distinct().ToArray();
        var orderIds = bills.Select(bill => bill.PurchaseOrderId).OfType<Guid>().Distinct().ToArray();
        var journalIds = bills.Select(bill => bill.JournalEntryId).OfType<Guid>().Distinct().ToArray();

        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.BusinessId == businessId && supplierIds.Contains(supplier.Id))
            .ToDictionaryAsync(supplier => supplier.Id, supplier => supplier.Name, cancellationToken).ConfigureAwait(false);
        var locations = await db.BusinessLocations.AsNoTracking()
            .Where(location => location.BusinessId == businessId && locationIds.Contains(location.Id))
            .ToDictionaryAsync(location => location.Id, location => location.Name, cancellationToken).ConfigureAwait(false);
        var orders = orderIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.PurchaseOrders.AsNoTracking()
                .Where(order => order.BusinessId == businessId && orderIds.Contains(order.Id))
                .ToDictionaryAsync(order => order.Id, order => order.OrderNumber, cancellationToken).ConfigureAwait(false);
        var journals = journalIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.JournalEntries.AsNoTracking()
                .Where(entry => entry.BusinessId == businessId && journalIds.Contains(entry.Id))
                .ToDictionaryAsync(entry => entry.Id, entry => entry.EntryNumber, cancellationToken).ConfigureAwait(false);

        var billLines = bills.SelectMany(bill => linesByBill.TryGetValue(bill.Id, out var rows) ? rows : [])
            .ToDictionary(line => line.Id);
        var billLineIds = billLines.Keys.ToArray();

        var creditLines = await db.SupplierCreditNoteLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && billLineIds.Contains(line.SupplierBillLineId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var creditedByLine = creditLines
            .GroupBy(line => line.SupplierBillLineId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        var creditedValueByBill = creditLines
            .Where(line => billLines.TryGetValue(line.SupplierBillLineId, out _))
            .GroupBy(line => billLines[line.SupplierBillLineId].SupplierBillId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.ValueAmount));

        var creditsByBill = await LoadCreditsAsync(db, businessId, billIds, billLines, cancellationToken).ConfigureAwait(false);

        return bills.Select(bill =>
        {
            var lines = linesByBill.TryGetValue(bill.Id, out var rows)
                ? rows
                : Array.Empty<SupplierBillLine>();
            var billCredits = creditsByBill.TryGetValue(bill.Id, out var found) ? found : [];

            return ToDto(bill, lines) with
            {
                SupplierName = suppliers.TryGetValue(bill.SupplierId, out var supplierName) ? supplierName : null,
                LocationName = locations.TryGetValue(bill.LocationId, out var locationName) ? locationName : null,
                OrderNumber = bill.PurchaseOrderId is { } order && orders.TryGetValue(order, out var number) ? number : null,
                JournalEntryNumber = bill.JournalEntryId is { } entry && journals.TryGetValue(entry, out var entryNumber)
                    ? entryNumber
                    : null,
                CreditedAmount = creditedValueByBill.TryGetValue(bill.Id, out var credited) ? credited : 0m,
                Credits = billCredits,
                Lines = [.. lines.Select(line => ToDtoLine(line, creditedByLine))],
            };
        }).ToArray();
    }

    private static SupplierBillLineDto ToDtoLine(SupplierBillLine line, IReadOnlyDictionary<Guid, decimal> creditedByLine)
        => new(line.Id, line.LineNo, line.PurchaseReceiptLineId, line.VariantId, line.ProductNameSnapshot,
            line.QuantityBilled, line.UnitCost, line.GrossAmount, line.DiscountAmount, line.TaxRate, line.TaxMode,
            line.NetAmount, line.TaxAmount, line.LineTotal,
            creditedByLine.TryGetValue(line.Id, out var credited) ? credited : 0m);

    /// <summary>Credits and their lines for a set of bills, newest first, with the documents each one names.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<SupplierCreditDto>>> LoadCreditsAsync(
        BillingDbContext db,
        Guid businessId,
        Guid[] billIds,
        IReadOnlyDictionary<Guid, SupplierBillLine> billLines,
        CancellationToken cancellationToken)
    {
        var credits = await db.SupplierCreditNotes.AsNoTracking()
            .Where(credit => credit.BusinessId == businessId && billIds.Contains(credit.SupplierBillId))
            .OrderByDescending(credit => credit.IssuedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (credits.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<SupplierCreditDto>>();
        }

        var creditIds = credits.Select(credit => credit.Id).ToArray();
        var receiptIds = credits.Select(credit => credit.SourcePurchaseReceiptId).OfType<Guid>().Distinct().ToArray();
        var journalIds = credits.Select(credit => credit.JournalEntryId).Distinct().ToArray();
        var lineRows = await db.SupplierCreditNoteLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId && creditIds.Contains(line.SupplierCreditNoteId))
            .OrderBy(line => line.LineNo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var billNumbers = await db.SupplierBills.AsNoTracking()
            .Where(bill => bill.BusinessId == businessId && billIds.Contains(bill.Id))
            .ToDictionaryAsync(bill => bill.Id, bill => bill.BillNumber, cancellationToken).ConfigureAwait(false);
        var receiptNumbers = receiptIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.PurchaseReceipts.AsNoTracking()
                .Where(receipt => receipt.BusinessId == businessId && receiptIds.Contains(receipt.Id))
                .ToDictionaryAsync(receipt => receipt.Id, receipt => receipt.ReceiptNumber, cancellationToken).ConfigureAwait(false);
        var journals = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && journalIds.Contains(entry.Id))
            .ToDictionaryAsync(entry => entry.Id, entry => entry.EntryNumber, cancellationToken).ConfigureAwait(false);
        var accountCodes = await AccountCodesByEntryAsync(db, businessId, journalIds, cancellationToken).ConfigureAwait(false);

        var byCredit = lineRows.GroupBy(line => line.SupplierCreditNoteId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        // What each bill line has left after the credits already taken: the figure the next credit is bounded by,
        // read in the order the credits were issued so a cumulative guard is visible rather than inferred.
        var remainingByBillLine = billLines.ToDictionary(pair => pair.Key, pair => pair.Value.QuantityBilled);

        var grouped = credits.GroupBy(credit => credit.SupplierBillId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(credit => credit.IssuedAtUtc)
                    .Select(credit => CreditDto(
                        credit,
                        billNumbers,
                        receiptNumbers,
                        journals,
                        accountCodes,
                        byCredit,
                        billLines,
                        remainingByBillLine))
                    .ToList());

        return grouped.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SupplierCreditDto>)pair.Value);
    }

    private static SupplierCreditDto CreditDto(
        SupplierCreditNote credit,
        IReadOnlyDictionary<Guid, string> billNumbers,
        IReadOnlyDictionary<Guid, string> receiptNumbers,
        IReadOnlyDictionary<Guid, string> journals,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> accountCodes,
        IReadOnlyDictionary<Guid, SupplierCreditNoteLine[]> linesByCredit,
        IReadOnlyDictionary<Guid, SupplierBillLine> billLines,
        IDictionary<Guid, decimal> remainingByBillLine)
        => ToCreditDto(
            credit,
            billNumbers.GetValueOrDefault(credit.SupplierBillId, string.Empty),
            credit.SourcePurchaseReceiptId is { } receipt && receiptNumbers.TryGetValue(receipt, out var receiptNumber)
                ? receiptNumber
                : null,
            journals.GetValueOrDefault(credit.JournalEntryId),
            accountCodes.GetValueOrDefault(credit.JournalEntryId),
            [.. linesByCredit.GetValueOrDefault(credit.Id, [])
                .Select(line => ToCreditLine(line, credit, billLines, remainingByBillLine))]);

    /// <summary>The accounts an entry actually moved, so a credit names its control treatment instead of implying it.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> AccountCodesByEntryAsync(
        BillingDbContext db,
        Guid businessId,
        Guid[] entryIds,
        CancellationToken cancellationToken)
    {
        if (entryIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var rows = await (from line in db.JournalLines.AsNoTracking()
                          where line.BusinessId == businessId && entryIds.Contains(line.JournalEntryId)
                          join account in db.Accounts.AsNoTracking() on line.AccountId equals account.Id
                          select new { line.JournalEntryId, account.Code })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.GroupBy(row => row.JournalEntryId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.Code).Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    private static SupplierCreditLineDto ToCreditLine(
        SupplierCreditNoteLine line,
        SupplierCreditNote credit,
        IReadOnlyDictionary<Guid, SupplierBillLine> billLines,
        IDictionary<Guid, decimal> remainingByBillLine)
    {
        var quantity = billLines.TryGetValue(line.SupplierBillLineId, out var billLine)
            ? billLine.QuantityBilled
            : line.Quantity;
        var alreadyCredited = remainingByBillLine.TryGetValue(line.SupplierBillLineId, out var left)
            ? quantity - left
            : 0m;

        // What the bill line holds after this credit: the screen bounds its next entry with it, so an over-credit is
        // caught before it is typed rather than answered with a refusal (task 27, recipe step 3).
        remainingByBillLine[line.SupplierBillLineId] = quantity - alreadyCredited - line.Quantity;

        return new SupplierCreditLineDto(
            line.Id, line.LineNo, line.SupplierBillLineId, line.ProductNameSnapshot, line.Quantity, line.UnitCost,
            line.BaseAmount, line.TaxRate, line.TaxAmount, line.ValueAmount, line.PurchaseReceiptLineId,
            remainingByBillLine[line.SupplierBillLineId]);
    }

    private static SupplierCreditDto ToCreditDto(
        SupplierCreditNote credit,
        string billNumber,
        string? sourceReceiptNumber,
        string? journalNumber,
        IReadOnlyList<string>? controlAccountCodes,
        IReadOnlyList<SupplierCreditLineDto> lines)
        => new(credit.Id, credit.CreditNumber, credit.SupplierBillId, billNumber, credit.SupplierId, credit.LocationId,
            credit.SourcePurchaseReceiptId, sourceReceiptNumber, credit.Reason, credit.Currency, credit.BaseAmount,
            credit.TaxAmount, credit.ValueAmount, credit.AppliedAmount, credit.BalanceDue, credit.JournalEntryId,
            journalNumber, controlAccountCodes, credit.IssuedAtUtc, lines);

    private static string? RequiredIdempotencyKey(HttpContext http)
    {
        var key = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        return key.Length is > 0 and <= 64 ? key : null;
    }

    private static IResult RequiredKeyProblem()
        => ApiResults.ValidationProblem("An idempotency key is required.",
            new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });

    private static IResult NotFoundBill()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The supplier bill was not found.");

    private static DomainRuleException BillNotFoundException()
        => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The supplier bill was not found.", "The requested supplier bill does not exist in this business.");

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
}
