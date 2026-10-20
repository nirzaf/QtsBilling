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

    public static IEndpointRouteBuilder MapSupplierBillEndpoints(this IEndpointRouteBuilder app)
    {
        var bills = app.MapGroup("/api/v1/purchasing/bills").WithTags("Purchasing");
        bills.MapGet("/", ListBillsAsync).RequireAuthorization(Permissions.PurchasingManage);
        bills.MapPost("/", CreateBillAsync).RequireAuthorization(Permissions.PurchasingManage);
        bills.MapGet("/{id:guid}", GetBillAsync).RequireAuthorization(Permissions.PurchasingManage);
        bills.MapPost("/{id:guid}/issue", IssueBillAsync).RequireAuthorization(Permissions.AccountingPost);

        app.MapPost("/api/v1/purchasing/supplier-payments", CreateSupplierPaymentAsync)
            .WithTags("Purchasing")
            .RequireAuthorization(Permissions.AccountingPost);

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

        return Results.Ok(rows.Select(bill => ToDto(bill, byBill.GetValueOrDefault(bill.Id) ?? [])).ToArray());
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
        return Results.Ok(ToDto(bill, lines));
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

        var errors = ValidateBillRequest(request);
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
                return (StatusCodes.Status201Created, ToDto(bill, billLineRows));
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

                return (StatusCodes.Status200OK, ToDto(bill, lines));
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
                var allocations = request.Allocations.OrderBy(item => item.SupplierBillId).ToArray();
                var billIds = allocations.Select(item => item.SupplierBillId).ToArray();
                var bills = await db.SupplierBills
                    .FromSql($"SELECT * FROM supplier_bills WHERE business_id = {businessId} AND id = ANY({billIds}) ORDER BY id FOR UPDATE")
                    .ToDictionaryAsync(bill => bill.Id, cancellationToken).ConfigureAwait(false);
                if (bills.Count != allocations.Length)
                {
                    throw new DomainRuleException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound,
                        "A supplier bill was not found.", "Every allocation must reference an open bill in this business.");
                }

                foreach (var allocation in allocations)
                {
                    var bill = bills[allocation.SupplierBillId];
                    if (bill.SupplierId != request.SupplierId
                        || !string.Equals(bill.Currency, request.Currency, StringComparison.OrdinalIgnoreCase)
                        || bill.Status is not (SupplierBillStatus.Issued or SupplierBillStatus.PartiallyPaid)
                        || allocation.Amount > bill.BalanceDue)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "A supplier payment allocation is invalid.", "Allocations must match the supplier and currency, target open bills, and not exceed each balance due.");
                    }

                    bill.AmountPaid += allocation.Amount;
                    bill.BalanceDue -= allocation.Amount;
                    bill.Status = bill.BalanceDue == 0m ? SupplierBillStatus.Paid : SupplierBillStatus.PartiallyPaid;
                }

                var accountCodes = method == PaymentMethod.Cash ? new[] { "2000", "1000" } : new[] { "2000", "1010" };
                var accounts = await LedgerWriter.ResolveAccountsAsync(db, businessId, accountCodes, cancellationToken).ConfigureAwait(false);
                if (accountCodes.Any(code => !accounts.ContainsKey(code)))
                {
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "Required supplier-payment ledger accounts are not configured.", "Configure AP and the selected cash or bank account before settling bills.");
                }

                var paymentId = Guid.CreateVersion7();
                var journal = await LedgerWriter.PostWithinTransactionAsync(db, businessId, userId, request.Currency,
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
                foreach (var allocation in allocations)
                {
                    db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
                    {
                        BusinessId = businessId,
                        SupplierPaymentId = payment.Id,
                        SupplierBillId = allocation.SupplierBillId,
                        AllocatedAmount = allocation.Amount,
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
                        allocationCount = allocations.Length,
                        journalEntryId = journal.Id,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (StatusCodes.Status201Created, new SupplierPaymentDto(
                    payment.Id, payment.SupplierId, payment.Method.ToString(), payment.Amount, payment.Currency,
                    payment.PaidOn, payment.Reference, payment.JournalEntryId, allocations));
            }, cancellationToken).ConfigureAwait(false);

        return Results.Json(execution.Body, statusCode: execution.StatusCode);
    }

    private static Dictionary<string, string[]> ValidateBillRequest(CreateSupplierBillRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SupplierId == Guid.Empty) errors[nameof(request.SupplierId)] = ["A supplier is required."];
        if (request.LocationId == Guid.Empty) errors[nameof(request.LocationId)] = ["A location is required."];
        if (request.BillDate == default) errors[nameof(request.BillDate)] = ["A valid supplier bill date is required."];
        if (request.DueDate is { } due && due < request.BillDate) errors[nameof(request.DueDate)] = ["The due date cannot precede the bill date."];
        if (request.SupplierInvoiceReference is { Length: > 128 }) errors[nameof(request.SupplierInvoiceReference)] = ["Supplier invoice references may contain at most 128 characters."];
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
            errors[nameof(request.Allocations)] = [$"A supplier payment requires 1-{MaxBillLines} bill allocations."];
            return errors;
        }

        if (request.Allocations.Any(item => item.SupplierBillId == Guid.Empty || item.Amount <= 0m || CommercialAmounts.HasMoreDecimals(item.Amount, 10_000))
            || request.Allocations.Select(item => item.SupplierBillId).Distinct().Count() != request.Allocations.Count)
        {
            errors[nameof(request.Allocations)] = ["Allocations must reference distinct bills and have positive amounts with at most four decimal places."];
        }
        else if (request.Allocations.Sum(item => item.Amount) != request.Amount)
        {
            errors[nameof(request.Allocations)] = ["The payment amount must exactly equal the sum of its allocations."];
        }

        return errors;
    }

    private static SupplierBillDto ToDto(SupplierBill bill, IReadOnlyList<SupplierBillLine> lines)
        => new(bill.Id, bill.BillNumber, EnumBridge.To<SupplierBillState>(bill.Status), bill.SupplierId,
            bill.LocationId, bill.PurchaseOrderId, bill.SupplierInvoiceReference, bill.BillDate, bill.DueDate,
            bill.Currency, bill.SubtotalAmount, bill.DiscountAmount, bill.TaxAmount, bill.RoundingAmount,
            bill.TotalAmount, bill.AmountPaid, bill.BalanceDue, bill.JournalEntryId,
            lines.Select(line => new SupplierBillLineDto(line.Id, line.LineNo, line.PurchaseReceiptLineId,
                line.VariantId, line.ProductNameSnapshot, line.QuantityBilled, line.UnitCost, line.GrossAmount, line.DiscountAmount,
                line.TaxRate, line.TaxMode, line.NetAmount, line.TaxAmount, line.LineTotal)).ToArray());

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
