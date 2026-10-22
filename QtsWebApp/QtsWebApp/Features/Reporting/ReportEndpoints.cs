using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Reports;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;
using QtsWebApp.Features.Sales;

namespace QtsWebApp.Features.Reporting;

/// <summary>
/// Financial and operational statements (QB-13) computed from authoritative facts only: posted journal
/// lines for the balance sheet, profit and loss and cash summaries, and inventory movements for the stock
/// summary. There is no stored balance to mutate - legacy mutable Assets/Liabilities/Equity CRUD is not
/// part of this host - and provisional offline captures are never included because they are not posted.
/// Every query is scoped to the signed-in business; only the business base currency can be reported.
/// </summary>
public static class ReportEndpoints
{
    private static readonly string[] CashAccountCodes = ["1000", "1010"];

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/v1/reports").WithTags("Reports");

        reports.MapGet("/ar-aging", ReceivableAgingAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/ap-aging", PayableAgingAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/tax-summary", TaxSummaryAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/z-report/{sessionId:guid}", ZReportAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/balance-sheet", BalanceSheetAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/profit-and-loss", ProfitAndLossAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/stock-summary", StockSummaryAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/cash-summary", CashSummaryAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/sales-summary", SalesSummaryAsync).RequireAuthorization(Permissions.ReportsView);

        return app;
    }

    private static async Task<IResult> ReceivableAgingAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? asOf,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, businessError) = await LoadBusinessAsync(db, businessId, currency: null, cancellationToken).ConfigureAwait(false);
        if (businessError is not null)
        {
            return businessError;
        }

        var settings = await db.BusinessAccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return AgingAccountsNotConfigured();
        }

        var agingDate = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var openInvoices = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.BusinessId == businessId
                && (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.PartiallyPaid)
                && invoice.BalanceDue > 0m)
            .Select(invoice => new
            {
                invoice.CustomerId,
                DueDate = invoice.DueDate ?? invoice.IssueDate,
                invoice.BalanceDue,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var openCreditSales = await db.Sales.AsNoTracking()
            .Where(sale => sale.BusinessId == businessId
                && sale.Status == SaleStatus.Posted
                && (sale.PaymentStatus == PaymentStatus.Pending || sale.PaymentStatus == PaymentStatus.Bounced)
                && sale.Currency == business!.BaseCurrency
                && !db.Invoices.Any(invoice => invoice.BusinessId == businessId
                    && invoice.SourceSaleId == sale.Id
                    && (invoice.Status == InvoiceStatus.Issued
                        || invoice.Status == InvoiceStatus.PartiallyPaid
                        || invoice.Status == InvoiceStatus.Paid)))
            .Select(sale => new { sale.CustomerId, sale.PostedAtUtc, sale.TotalAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openImportedReceivablesRaw = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId
                && item.Kind == OpenItemKind.Receivable
                && item.Currency == business!.BaseCurrency)
            .Select(item => new
            {
                item.Id,
                item.CustomerId,
                DueDate = item.DueDate ?? item.DocumentDate,
                item.OriginalAmount,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // An opening balance is now settleable from a customer's deposit (specification task 25), so the aging must
        // report what is still owing on it rather than the amount the import recorded. Left as the original, the
        // receivable report would count money twice: once as cash the customer already paid in, once as a debt they
        // still owe - and the control account would no longer agree with the list.
        var appliedToOpenings = openImportedReceivablesRaw.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await db.PaymentAllocations.AsNoTracking()
                .Where(allocation => allocation.BusinessId == businessId
                    && allocation.OpenItemId != null
                    && openImportedReceivablesRaw.Select(item => item.Id).Contains(allocation.OpenItemId.Value))
                .GroupBy(allocation => allocation.OpenItemId!.Value)
                .Select(group => new { Id = group.Key, Applied = group.Sum(item => item.AllocatedAmount) })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(row => row.Id, row => row.Applied);

        var openImportedReceivables = openImportedReceivablesRaw
            .Select(item => new
            {
                item.CustomerId,
                item.DueDate,
                Balance = Math.Max(0m, item.OriginalAmount - appliedToOpenings.GetValueOrDefault(item.Id)),
            })
            .Where(item => item.Balance > 0m)
            .ToList();

        var customerIds = openInvoices.Where(invoice => invoice.CustomerId.HasValue)
            .Select(invoice => invoice.CustomerId!.Value)
            .Concat(openCreditSales.Where(sale => sale.CustomerId.HasValue).Select(sale => sale.CustomerId!.Value))
            .Concat(openImportedReceivables.Where(item => item.CustomerId.HasValue).Select(item => item.CustomerId!.Value))
            .Distinct().ToArray();
        var customers = await db.Customers.AsNoTracking()
            .Where(customer => customer.BusinessId == businessId && customerIds.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, customer => new { customer.Name, customer.PaymentTermsDays }, cancellationToken).ConfigureAwait(false);

        var openBalances = openInvoices
            .Select(invoice => (CustomerId: invoice.CustomerId, DueDate: invoice.DueDate, Balance: invoice.BalanceDue))
            .Concat(openImportedReceivables.Select(item => (CustomerId: item.CustomerId, DueDate: item.DueDate, Balance: item.Balance)))
            .Concat(openCreditSales.Select(sale =>
            {
                var termsDays = sale.CustomerId is { } id ? customers.GetValueOrDefault(id)?.PaymentTermsDays ?? 0 : 0;
                var saleDate = LedgerWriter.LocalDate(business!.TimeZone, sale.PostedAtUtc);
                return (CustomerId: sale.CustomerId, DueDate: saleDate.AddDays(Math.Max(0, termsDays)), Balance: sale.TotalAmount);
            }))
            .ToArray();

        var customerRows = openBalances.GroupBy(invoice => invoice.CustomerId)
            .Select(group =>
            {
                var buckets = BuildBuckets(group.Select(invoice => (invoice.DueDate, invoice.Balance)), agingDate);
                return new ReceivableAgingRowDto(
                    group.Key,
                    group.Key is { } customerId ? customers.GetValueOrDefault(customerId)?.Name ?? "Unknown customer" : "Unassigned customer",
                    buckets.Current,
                    buckets.Days1To30,
                    buckets.Days31To60,
                    buckets.Days61To90,
                    buckets.DaysOver90,
                    buckets.Balance);
            })
            .OrderBy(row => row.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var accountSums = await PostedSumsAsync(db, businessId, [settings.AccountsReceivableAccountId], null, null, cancellationToken).ConfigureAwait(false);
        var ledgerReceivable = LedgerWriter.Round(
            accountSums.GetValueOrDefault(settings.AccountsReceivableAccountId).Debit
            - accountSums.GetValueOrDefault(settings.AccountsReceivableAccountId).Credit);
        var totalOpen = LedgerWriter.Round(customerRows.Sum(row => row.Balance));
        var variance = LedgerWriter.Round(totalOpen - ledgerReceivable);

        return ReportResponse(format, new ReceivableAgingDto(
            businessId,
            agingDate,
            business!.BaseCurrency,
            customerRows,
            totalOpen,
            ledgerReceivable,
            variance,
            variance == 0m,
            IncludesProvisional: false), "ar-aging.csv");
    }

    private static async Task<IResult> PayableAgingAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? asOf,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, businessError) = await LoadBusinessAsync(db, businessId, currency: null, cancellationToken).ConfigureAwait(false);
        if (businessError is not null)
        {
            return businessError;
        }

        var settings = await db.BusinessAccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return AgingAccountsNotConfigured();
        }

        var agingDate = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var openBills = await db.SupplierBills.AsNoTracking()
            .Where(bill => bill.BusinessId == businessId
                && (bill.Status == SupplierBillStatus.Issued || bill.Status == SupplierBillStatus.PartiallyPaid)
                && bill.BalanceDue > 0m)
            .Select(bill => new
            {
                bill.SupplierId,
                DueDate = bill.DueDate ?? bill.BillDate,
                bill.BalanceDue,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var openImportedPayables = await db.OpenItems.AsNoTracking()
            .Where(item => item.BusinessId == businessId
                && item.Kind == OpenItemKind.Payable
                && item.Currency == business!.BaseCurrency)
            .Select(item => new
            {
                item.SupplierId,
                DueDate = item.DueDate ?? item.DocumentDate,
                Balance = item.OriginalAmount,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var supplierIds = openBills.Select(bill => bill.SupplierId)
            .Concat(openImportedPayables.Where(item => item.SupplierId.HasValue).Select(item => item.SupplierId!.Value))
            .Distinct().ToArray();
        var supplierNames = await db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.BusinessId == businessId && supplierIds.Contains(supplier.Id))
            .ToDictionaryAsync(supplier => supplier.Id, supplier => supplier.Name, cancellationToken).ConfigureAwait(false);

        var openPayables = openBills
            .Select(bill => (SupplierId: bill.SupplierId, bill.DueDate, Balance: bill.BalanceDue))
            .Concat(openImportedPayables.Where(item => item.SupplierId.HasValue)
                .Select(item => (SupplierId: item.SupplierId!.Value, item.DueDate, item.Balance)))
            .ToArray();
        var supplierRows = openPayables.GroupBy(bill => bill.SupplierId)
            .Select(group =>
            {
                var buckets = BuildBuckets(group.Select(bill => (bill.DueDate, bill.Balance)), agingDate);
                return new PayableAgingRowDto(
                    group.Key,
                    supplierNames.GetValueOrDefault(group.Key, "Unknown supplier"),
                    buckets.Current,
                    buckets.Days1To30,
                    buckets.Days31To60,
                    buckets.Days61To90,
                    buckets.DaysOver90,
                    buckets.Balance);
            })
            .OrderBy(row => row.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var accountIds = new[] { settings.AccountsPayableAccountId, settings.GrniClearingAccountId };
        var accountSums = await PostedSumsAsync(db, businessId, accountIds, null, null, cancellationToken).ConfigureAwait(false);
        var apSums = accountSums.GetValueOrDefault(settings.AccountsPayableAccountId);
        var grniSums = accountSums.GetValueOrDefault(settings.GrniClearingAccountId);
        var ledgerPayables = LedgerWriter.Round(apSums.Credit - apSums.Debit);
        var openGrni = LedgerWriter.Round(grniSums.Credit - grniSums.Debit);
        var totalOpen = LedgerWriter.Round(supplierRows.Sum(row => row.Balance));
        var totalExposure = LedgerWriter.Round(totalOpen + openGrni);
        var ledgerExposure = LedgerWriter.Round(ledgerPayables + openGrni);
        var variance = LedgerWriter.Round(totalExposure - ledgerExposure);

        return ReportResponse(format, new PayableAgingDto(
            businessId,
            agingDate,
            business!.BaseCurrency,
            supplierRows,
            totalOpen,
            ledgerPayables,
            openGrni,
            totalExposure,
            ledgerExposure,
            variance,
            variance == 0m,
            IncludesProvisional: false), "ap-aging.csv");
    }

    private static AgingBucketTotals BuildBuckets(IEnumerable<(DateOnly DueDate, decimal Balance)> balances, DateOnly asOf)
    {
        decimal current = 0m;
        decimal days1To30 = 0m;
        decimal days31To60 = 0m;
        decimal days61To90 = 0m;
        decimal daysOver90 = 0m;

        foreach (var (dueDate, balance) in balances)
        {
            var daysPastDue = asOf.DayNumber - dueDate.DayNumber;
            if (daysPastDue <= 0) current += balance;
            else if (daysPastDue <= 30) days1To30 += balance;
            else if (daysPastDue <= 60) days31To60 += balance;
            else if (daysPastDue <= 90) days61To90 += balance;
            else daysOver90 += balance;
        }

        return new AgingBucketTotals(
            LedgerWriter.Round(current),
            LedgerWriter.Round(days1To30),
            LedgerWriter.Round(days31To60),
            LedgerWriter.Round(days61To90),
            LedgerWriter.Round(daysOver90),
            LedgerWriter.Round(current + days1To30 + days31To60 + days61To90 + daysOver90));
    }

    private sealed record AgingBucketTotals(
        decimal Current,
        decimal Days1To30,
        decimal Days31To60,
        decimal Days61To90,
        decimal DaysOver90,
        decimal Balance);

    private static IResult AgingAccountsNotConfigured()
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.Conflict,
            "Aging reports require the business receivable, payable, and GRNI control accounts.",
            "Save the accounting settings before requesting an AR or AP aging report.");

    // ---------------------------------------------------------------- tax and register close

    private static async Task<IResult> TaxSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, businessError) = await LoadBusinessAsync(db, businessId, currency: null, cancellationToken).ConfigureAwait(false);
        if (businessError is not null)
        {
            return businessError;
        }

        var reportBusiness = business!;
        var rangeTo = to ?? LedgerWriter.LocalDate(reportBusiness.TimeZone, DateTimeOffset.UtcNow);
        var rangeFrom = from ?? rangeTo.AddYears(-1);
        if (rangeFrom > rangeTo)
        {
            return InvalidRange();
        }

        var fromUtc = BusinessDateStartUtc(reportBusiness.TimeZone, rangeFrom);
        var toUtcExclusive = BusinessDateStartUtc(reportBusiness.TimeZone, rangeTo.AddDays(1));
        var output = new List<TaxFact>();
        var input = new List<TaxFact>();

        var saleLines = await (from line in db.SaleLines.AsNoTracking()
                               join sale in db.Sales.AsNoTracking() on new { line.SaleId, line.BusinessId } equals new { SaleId = sale.Id, sale.BusinessId }
                               where sale.BusinessId == businessId
                                   && (sale.Status == SaleStatus.Posted || sale.Status == SaleStatus.Reversed)
                                   && sale.PostedAtUtc >= fromUtc && sale.PostedAtUtc < toUtcExclusive
                                   && !db.Invoices.Any(invoice => invoice.BusinessId == businessId
                                       && invoice.SourceSaleId == sale.Id
                                       && invoice.Status != InvoiceStatus.Draft
                                       && invoice.Status != InvoiceStatus.Void)
                               select new { Line = line, sale.PostedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in saleLines)
        {
            output.Add(new TaxFact(row.Line.TaxRate, SalePosting.EffectiveTaxMode(row.Line), row.Line.TaxAmount));
        }

        var refundLines = await (from refund in db.Refunds.AsNoTracking()
                                 join refundLine in db.RefundLines.AsNoTracking() on new { RefundId = refund.Id, refund.BusinessId } equals new { refundLine.RefundId, refundLine.BusinessId }
                                 join saleLine in db.SaleLines.AsNoTracking() on new { refundLine.SaleLineId, refundLine.BusinessId } equals new { SaleLineId = saleLine.Id, saleLine.BusinessId }
                                 where refund.BusinessId == businessId
                                     && refund.PostedAtUtc >= fromUtc && refund.PostedAtUtc < toUtcExclusive
                                 select new { SaleLine = saleLine, refundLine.TaxAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in refundLines)
        {
            output.Add(new TaxFact(row.SaleLine.TaxRate, SalePosting.EffectiveTaxMode(row.SaleLine), -row.TaxAmount));
        }

        var invoiceLines = await (from line in db.InvoiceLines.AsNoTracking()
                                  join invoice in db.Invoices.AsNoTracking() on new { line.InvoiceId, line.BusinessId } equals new { InvoiceId = invoice.Id, invoice.BusinessId }
                                  where invoice.BusinessId == businessId
                                      && invoice.IssueDate >= rangeFrom && invoice.IssueDate <= rangeTo
                                      && invoice.Status != InvoiceStatus.Draft
                                      && (invoice.Status != InvoiceStatus.Void
                                          || db.CreditNotes.Any(note => note.BusinessId == businessId && note.InvoiceId == invoice.Id))
                                  select new { line.TaxRate, line.TaxAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        output.AddRange(invoiceLines.Select(row => new TaxFact(row.TaxRate, "Exclusive", row.TaxAmount)));

        var creditNoteLines = await (from note in db.CreditNotes.AsNoTracking()
                                     join creditLine in db.CreditNoteLines.AsNoTracking() on new { CreditNoteId = note.Id, note.BusinessId } equals new { creditLine.CreditNoteId, creditLine.BusinessId }
                                     join line in db.InvoiceLines.AsNoTracking() on new { creditLine.InvoiceLineId, creditLine.BusinessId } equals new { InvoiceLineId = line.Id, line.BusinessId }
                                     where note.BusinessId == businessId && note.IssueDate >= rangeFrom && note.IssueDate <= rangeTo
                                     select new { line.TaxRate, creditLine.TaxAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        output.AddRange(creditNoteLines.Select(row => new TaxFact(row.TaxRate, "Exclusive", -row.TaxAmount)));

        var supplierBillLines = await (from line in db.SupplierBillLines.AsNoTracking()
                                       join bill in db.SupplierBills.AsNoTracking() on new { line.SupplierBillId, line.BusinessId } equals new { SupplierBillId = bill.Id, bill.BusinessId }
                                       where bill.BusinessId == businessId
                                           && bill.BillDate >= rangeFrom && bill.BillDate <= rangeTo
                                           && (bill.Status == SupplierBillStatus.Issued
                                               || bill.Status == SupplierBillStatus.PartiallyPaid
                                               || bill.Status == SupplierBillStatus.Paid)
                                       select new TaxFact(line.TaxRate, line.TaxMode, line.TaxAmount))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        input.AddRange(supplierBillLines);

        var expenses = await db.Expenses.AsNoTracking()
            .Where(expense => expense.BusinessId == businessId && expense.OccurredOn >= rangeFrom && expense.OccurredOn <= rangeTo)
            .Select(expense => new TaxFact(expense.TaxRate, expense.TaxMode, expense.TaxAmount))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        input.AddRange(expenses);

        var reversedExpenses = await (from reversal in db.ExpenseReversals.AsNoTracking()
                                      join expense in db.Expenses.AsNoTracking() on new { reversal.ExpenseId, reversal.BusinessId } equals new { ExpenseId = expense.Id, expense.BusinessId }
                                      join entry in db.JournalEntries.AsNoTracking() on new { JournalEntryId = reversal.JournalEntryId, reversal.BusinessId } equals new { JournalEntryId = entry.Id, entry.BusinessId }
                                      where reversal.BusinessId == businessId
                                          && entry.Status == EntryStatus.Posted
                                          && entry.EntryDate >= rangeFrom && entry.EntryDate <= rangeTo
                                      select new TaxFact(expense.TaxRate, expense.TaxMode, -expense.TaxAmount))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        input.AddRange(reversedExpenses);

        var grouped = output.Select(fact => new { fact.TaxRate, fact.TaxMode, OutputTax = fact.Amount, InputTax = 0m })
            .Concat(input.Select(fact => new { fact.TaxRate, fact.TaxMode, OutputTax = 0m, InputTax = fact.Amount }))
            .GroupBy(fact => (fact.TaxRate, fact.TaxMode))
            .Select(group => new TaxSummaryRowDto(
                group.Key.TaxRate,
                group.Key.TaxMode,
                LedgerWriter.Round(group.Sum(fact => fact.OutputTax)),
                LedgerWriter.Round(group.Sum(fact => fact.InputTax)),
                LedgerWriter.Round(group.Sum(fact => fact.OutputTax) - group.Sum(fact => fact.InputTax))))
            .OrderBy(row => row.TaxRate)
            .ThenBy(row => row.TaxMode, StringComparer.Ordinal)
            .ToArray();

        var totalOutput = LedgerWriter.Round(grouped.Sum(row => row.OutputTaxAmount));
        var totalInput = LedgerWriter.Round(grouped.Sum(row => row.InputTaxAmount));
        return ReportResponse(format, new TaxSummaryDto(
            businessId,
            rangeFrom,
            rangeTo,
            reportBusiness.BaseCurrency,
            grouped,
            totalOutput,
            totalInput,
            LedgerWriter.Round(totalOutput - totalInput),
            IncludesProvisional: false), "tax-summary.csv");
    }

    private static async Task<IResult> ZReportAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid sessionId,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var session = await db.CashRegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The drawer session does not exist in this business.");
        }

        var register = await db.CashRegisters.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == session.CashRegisterId, cancellationToken)
            .ConfigureAwait(false);
        if (register is null)
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The drawer register for this session is unavailable.");
        }

        var movements = await db.CashRegisterMovements.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.CashRegisterSessionId == session.Id)
            .Select(row => new { row.MovementType, row.Amount, row.SaleId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        decimal MovementSum(CashMovementType kind) => LedgerWriter.Round(movements.Where(row => row.MovementType == kind).Sum(row => row.Amount));

        var paymentRows = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.CashRegisterSessionId == session.Id
                && payment.SaleId != null)
            .Select(payment => new { payment.SaleId, payment.Method, payment.Status, payment.Amount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var saleIds = paymentRows.Where(row => row.SaleId != null).Select(row => row.SaleId!.Value)
            .Concat(movements.Where(row => row.MovementType == CashMovementType.SalePayment && row.SaleId != null).Select(row => row.SaleId!.Value))
            .Distinct().ToArray();
        var sales = saleIds.Length == 0
            ? new List<Sale>()
            : await db.Sales.AsNoTracking()
                .Where(sale => sale.BusinessId == businessId && saleIds.Contains(sale.Id)
                    && (sale.Status == SaleStatus.Posted || sale.Status == SaleStatus.Reversed))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        var collectedPayments = paymentRows
            .Where(row => row.Status == PaymentStatus.Confirmed || row.Status == PaymentStatus.Refunded)
            .GroupBy(row => row.Method)
            .Select(group => new PaymentMethodTotalDto(group.Key.ToString(), group.Count(), LedgerWriter.Round(group.Sum(row => row.Amount))))
            .OrderBy(row => row.Method, StringComparer.Ordinal).ToArray();

        var refunds = await db.Refunds.AsNoTracking()
            .Where(refund => refund.BusinessId == businessId && refund.CashRegisterSessionId == session.Id)
            .Select(refund => new { refund.Method, refund.Amount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var refundsByMethod = refunds.GroupBy(row => row.Method)
            .Select(group => new PaymentMethodTotalDto(group.Key.ToString(), group.Count(), LedgerWriter.Round(group.Sum(row => row.Amount))))
            .OrderBy(row => row.Method, StringComparer.Ordinal).ToArray();

        var gross = LedgerWriter.Round(sales.Sum(sale => sale.SubtotalAmount));
        var discounts = LedgerWriter.Round(sales.Sum(sale => sale.DiscountAmount));
        var tax = LedgerWriter.Round(sales.Sum(sale => sale.TaxAmount));
        var netSales = LedgerWriter.Round(gross - discounts);
        var expectedCash = LedgerWriter.Round(session.OpeningFloat
            + MovementSum(CashMovementType.SalePayment)
            + MovementSum(CashMovementType.CashIn)
            - MovementSum(CashMovementType.CashOut)
            - MovementSum(CashMovementType.Refund));
        var variance = session.CountedCash is { } counted
            ? LedgerWriter.Round(counted - (session.ExpectedCash ?? expectedCash))
            : (decimal?)null;

        return ReportResponse(format, new ZReportDto(
            businessId,
            session.Id,
            register.Id,
            register.Name,
            register.LocationId,
            session.Currency,
            session.Status.ToString(),
            session.OpenedAtUtc,
            session.ClosedAtUtc,
            session.OpeningFloat,
            sales.Count,
            gross,
            discounts,
            tax,
            netSales,
            LedgerWriter.Round(sales.Sum(sale => sale.TotalAmount)),
            collectedPayments,
            refunds.Count,
            LedgerWriter.Round(refunds.Sum(row => row.Amount)),
            refundsByMethod,
            MovementSum(CashMovementType.CashIn),
            MovementSum(CashMovementType.CashOut),
            session.ExpectedCash ?? expectedCash,
            session.CountedCash,
            session.Variance ?? variance,
            IncludesProvisional: false), "z-report.csv");
    }

    private static DateTimeOffset BusinessDateStartUtc(string? timeZoneId, DateOnly date)
    {
        var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        try
        {
            var zone = string.IsNullOrWhiteSpace(timeZoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero);
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(localStart, DateTimeKind.Utc));
        }
        catch (InvalidTimeZoneException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(localStart, DateTimeKind.Utc));
        }
        catch (ArgumentException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(localStart, DateTimeKind.Utc));
        }
    }

    // ---------------------------------------------------------------- balance sheet

    private static async Task<IResult> BalanceSheetAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? asOf,
        string? currency,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, error) = await LoadBusinessAsync(db, businessId, currency, cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var cutOff = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var activity = await PostedActivityAsync(db, businessId, null, cutOff, cancellationToken).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId)
            .ToDictionaryAsync(account => account.Id, cancellationToken)
            .ConfigureAwait(false);

        var assets = new List<StatementAccountDto>();
        var liabilities = new List<StatementAccountDto>();
        var equity = new List<StatementAccountDto>();
        var totalRevenue = 0m;
        var totalExpenses = 0m;

        foreach (var item in activity)
        {
            if (!accounts.TryGetValue(item.AccountId, out var account))
            {
                continue;
            }

            var kind = EnumBridge.To<AccountKind>(account.AccountType);
            var debitNormal = kind is AccountKind.Asset or AccountKind.Expense;
            var amount = LedgerWriter.Round(debitNormal ? item.Debit - item.Credit : item.Credit - item.Debit);

            switch (kind)
            {
                case AccountKind.Asset:
                    if (amount != 0)
                    {
                        assets.Add(new StatementAccountDto(account.Id, account.Code, account.Name, amount));
                    }

                    break;
                case AccountKind.Liability:
                    if (amount != 0)
                    {
                        liabilities.Add(new StatementAccountDto(account.Id, account.Code, account.Name, amount));
                    }

                    break;
                case AccountKind.Equity:
                    if (amount != 0)
                    {
                        equity.Add(new StatementAccountDto(account.Id, account.Code, account.Name, amount));
                    }

                    break;
                case AccountKind.Revenue:
                    totalRevenue += amount;
                    break;
                case AccountKind.Expense:
                    totalExpenses += amount;
                    break;
            }
        }

        // Revenue and expense accounts are not closed to equity by any stored step, so the current earnings
        // they hold are presented as part of equity - the books balance without mutating a balance.
        var currentEarnings = LedgerWriter.Round(totalRevenue - totalExpenses);
        var totalAssets = LedgerWriter.Round(assets.Sum(account => account.Amount));
        var totalLiabilities = LedgerWriter.Round(liabilities.Sum(account => account.Amount));
        var totalEquity = LedgerWriter.Round(equity.Sum(account => account.Amount) + currentEarnings);
        var totalLiabilitiesAndEquity = LedgerWriter.Round(totalLiabilities + totalEquity);

        return ReportResponse(format, new BalanceSheetDto(
            businessId,
            cutOff,
            business!.BaseCurrency,
            assets.OrderBy(account => account.Code).ToList(),
            liabilities.OrderBy(account => account.Code).ToList(),
            equity.OrderBy(account => account.Code).ToList(),
            totalAssets,
            totalLiabilities,
            totalEquity,
            currentEarnings,
            totalLiabilitiesAndEquity,
            totalAssets == totalLiabilitiesAndEquity,
            IncludesProvisional: false), "balance-sheet.csv");
    }

    // ---------------------------------------------------------------- profit and loss

    private static async Task<IResult> ProfitAndLossAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? currency,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, error) = await LoadBusinessAsync(db, businessId, currency, cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var rangeTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeFrom = from ?? rangeTo.AddYears(-1);

        if (rangeFrom > rangeTo)
        {
            return InvalidRange();
        }

        var activity = await PostedActivityAsync(db, businessId, rangeFrom, rangeTo, cancellationToken).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId)
            .ToDictionaryAsync(account => account.Id, cancellationToken)
            .ConfigureAwait(false);

        var revenue = new List<StatementAccountDto>();
        var expenses = new List<StatementAccountDto>();

        foreach (var item in activity)
        {
            if (!accounts.TryGetValue(item.AccountId, out var account))
            {
                continue;
            }

            var kind = EnumBridge.To<AccountKind>(account.AccountType);

            if (kind == AccountKind.Revenue)
            {
                var amount = LedgerWriter.Round(item.Credit - item.Debit);

                if (amount != 0)
                {
                    revenue.Add(new StatementAccountDto(account.Id, account.Code, account.Name, amount));
                }
            }
            else if (kind == AccountKind.Expense)
            {
                var amount = LedgerWriter.Round(item.Debit - item.Credit);

                if (amount != 0)
                {
                    expenses.Add(new StatementAccountDto(account.Id, account.Code, account.Name, amount));
                }
            }
        }

        var totalRevenue = LedgerWriter.Round(revenue.Sum(account => account.Amount));
        var totalExpenses = LedgerWriter.Round(expenses.Sum(account => account.Amount));

        return ReportResponse(format, new ProfitAndLossDto(
            businessId,
            rangeFrom,
            rangeTo,
            business!.BaseCurrency,
            revenue.OrderBy(account => account.Code).ToList(),
            expenses.OrderBy(account => account.Code).ToList(),
            totalRevenue,
            totalExpenses,
            LedgerWriter.Round(totalRevenue - totalExpenses),
            IncludesProvisional: false), "profit-and-loss.csv");
    }

    // ---------------------------------------------------------------- stock summary

    private static async Task<IResult> StockSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? asOf,
        Guid? locationId,
        string? currency,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, error) = await LoadBusinessAsync(db, businessId, currency, cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var cutOff = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var cutOffExclusive = new DateTimeOffset(cutOff.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        if (locationId is { } filter)
        {
            var known = await db.BusinessLocations.AsNoTracking()
                .AnyAsync(location => location.BusinessId == businessId && location.Id == filter, cancellationToken)
                .ConfigureAwait(false);

            if (!known)
            {
                return ApiResults.Problem(
                    StatusCodes.Status404NotFound,
                    ApiErrorCodes.NotFound,
                    "The location does not exist in this business.",
                    "Stock is reported per business-scoped location; cross-business identifiers are denied.");
            }
        }

        var groups = await db.InventoryMovements.AsNoTracking()
            .Where(movement => movement.BusinessId == businessId
                && movement.OccurredAtUtc < cutOffExclusive
                && (locationId == null || movement.LocationId == locationId))
            .GroupBy(movement => new { movement.LocationId, movement.VariantId })
            .Select(grouped => new
            {
                grouped.Key.LocationId,
                grouped.Key.VariantId,
                Quantity = grouped.Sum(movement => movement.QuantityDelta),
                Value = grouped.Sum(movement => movement.QuantityDelta * movement.UnitCost),
                LastMovementAtUtc = grouped.Max(movement => movement.OccurredAtUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var locations = await db.BusinessLocations.AsNoTracking()
            .Where(location => location.BusinessId == businessId)
            .ToDictionaryAsync(location => location.Id, location => location.Name, cancellationToken)
            .ConfigureAwait(false);

        var variantIds = groups.Select(grouped => grouped.VariantId).Distinct().ToArray();

        var variants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.BusinessId == businessId && variantIds.Contains(variant.Id))
            .Select(variant => new { variant.Id, variant.Name, variant.Sku })
            .ToDictionaryAsync(variant => variant.Id, cancellationToken)
            .ConfigureAwait(false);

        var rows = groups
            .Where(grouped => locations.ContainsKey(grouped.LocationId) && variants.ContainsKey(grouped.VariantId))
            .Select(grouped => new StockSummaryRowDto(
                grouped.LocationId,
                locations[grouped.LocationId],
                grouped.VariantId,
                variants[grouped.VariantId].Name,
                variants[grouped.VariantId].Sku,
                LedgerWriter.Round(grouped.Quantity),
                LedgerWriter.Round(grouped.Value),
                grouped.LastMovementAtUtc))
            .OrderBy(row => row.LocationName)
            .ThenBy(row => row.VariantName)
            .ToList();

        return ReportResponse(format, new StockSummaryDto(
            businessId,
            cutOff,
            business!.BaseCurrency,
            rows,
            LedgerWriter.Round(rows.Sum(row => row.OnHand)),
            LedgerWriter.Round(rows.Sum(row => row.StockValue)),
            IncludesProvisional: false), "stock-summary.csv");
    }

    // ---------------------------------------------------------------- cash summary

    private static async Task<IResult> CashSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? currency,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, error) = await LoadBusinessAsync(db, businessId, currency, cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var rangeTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeFrom = from ?? rangeTo.AddYears(-1);

        if (rangeFrom > rangeTo)
        {
            return InvalidRange();
        }

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId && CashAccountCodes.Contains(account.Code))
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var accountIds = accounts.Select(account => account.Id).ToArray();

        var opening = await PostedSumsAsync(db, businessId, accountIds, null, rangeFrom.AddDays(-1), cancellationToken).ConfigureAwait(false);
        var period = await PostedSumsAsync(db, businessId, accountIds, rangeFrom, rangeTo, cancellationToken).ConfigureAwait(false);

        var rows = accounts
            .Select(account =>
            {
                opening.TryGetValue(account.Id, out var before);
                period.TryGetValue(account.Id, out var inside);

                var openingBalance = LedgerWriter.Round(before.Debit - before.Credit);
                var debits = LedgerWriter.Round(inside.Debit);
                var credits = LedgerWriter.Round(inside.Credit);

                return new CashSummaryRowDto(
                    account.Id,
                    account.Code,
                    account.Name,
                    openingBalance,
                    debits,
                    credits,
                    LedgerWriter.Round(openingBalance + debits - credits));
            })
            .ToList();

        return ReportResponse(format, new CashSummaryDto(
            businessId,
            rangeFrom,
            rangeTo,
            business!.BaseCurrency,
            rows,
            LedgerWriter.Round(rows.Sum(row => row.OpeningBalance)),
            LedgerWriter.Round(rows.Sum(row => row.Debits)),
            LedgerWriter.Round(rows.Sum(row => row.Credits)),
            LedgerWriter.Round(rows.Sum(row => row.ClosingBalance)),
            IncludesProvisional: false), "cash-summary.csv");
    }

    // ---------------------------------------------------------------- sales summary

    private static async Task<IResult> SalesSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? currency,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var (business, error) = await LoadBusinessAsync(db, businessId, currency, cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return error;
        }

        var rangeTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeFrom = from ?? rangeTo.AddYears(-1);

        if (rangeFrom > rangeTo)
        {
            return InvalidRange();
        }

        var fromUtc = new DateTimeOffset(rangeFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtcExclusive = new DateTimeOffset(rangeTo.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var sales = await db.Sales.AsNoTracking()
            .Where(sale => sale.BusinessId == businessId
                && sale.Status == SaleStatus.Posted
                && sale.PostedAtUtc >= fromUtc
                && sale.PostedAtUtc < toUtcExclusive)
            .Select(sale => new
            {
                sale.SubtotalAmount,
                sale.DiscountAmount,
                sale.TaxAmount,
                sale.TotalAmount,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId
                && payment.SaleId != null
                && payment.Status == PaymentStatus.Confirmed
                && payment.ReceivedAtUtc >= fromUtc
                && payment.ReceivedAtUtc < toUtcExclusive)
            .Select(payment => new { payment.Method, payment.Amount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byMethod = payments
            .GroupBy(payment => payment.Method)
            .Select(grouped => new PaymentMethodTotalDto(
                grouped.Key.ToString(),
                grouped.Count(),
                LedgerWriter.Round(grouped.Sum(payment => payment.Amount))))
            .OrderBy(total => total.Method)
            .ToList();

        return ReportResponse(format, new SalesSummaryDto(
            businessId,
            rangeFrom,
            rangeTo,
            business!.BaseCurrency,
            sales.Count,
            LedgerWriter.Round(sales.Sum(sale => sale.SubtotalAmount)),
            LedgerWriter.Round(sales.Sum(sale => sale.DiscountAmount)),
            LedgerWriter.Round(sales.Sum(sale => sale.TaxAmount)),
            LedgerWriter.Round(sales.Sum(sale => sale.TotalAmount)),
            byMethod,
            IncludesProvisional: false), "sales-summary.csv");
    }

    // ---------------------------------------------------------------- shared helpers

    private static async Task<(Business? Business, IResult? Error)> LoadBusinessAsync(
        BillingDbContext db,
        Guid businessId,
        string? currency,
        CancellationToken cancellationToken)
    {
        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return (null, BusinessScope.MissingMembership());
        }

        if (!string.IsNullOrWhiteSpace(currency)
            && !string.Equals(currency.Trim(), business.BaseCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return (business, ApiResults.ValidationProblem(
                "The currency filter is invalid.",
                new Dictionary<string, string[]>
                {
                    ["currency"] = [$"Statements are reported in the business base currency ({business.BaseCurrency}); other presentational currencies are out of scope."],
                }));
        }

        return (business, null);
    }

    private static async Task<List<AccountActivity>> PostedActivityAsync(
        BillingDbContext db,
        Guid businessId,
        DateOnly? rangeFrom,
        DateOnly? rangeTo,
        CancellationToken cancellationToken)
        => await (
                from line in db.JournalLines.AsNoTracking()
                join entry in db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                where line.BusinessId == businessId
                    && entry.BusinessId == businessId
                    && entry.Status == EntryStatus.Posted
                    && (rangeFrom == null || entry.EntryDate >= rangeFrom)
                    && (rangeTo == null || entry.EntryDate <= rangeTo)
                group line by line.AccountId
                into grouped
                select new AccountActivity(
                    grouped.Key,
                    grouped.Sum(line => line.DebitAmount),
                    grouped.Sum(line => line.CreditAmount)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private static async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> PostedSumsAsync(
        BillingDbContext db,
        Guid businessId,
        Guid[] accountIds,
        DateOnly? rangeFrom,
        DateOnly? rangeTo,
        CancellationToken cancellationToken)
    {
        var activity = await (
                from line in db.JournalLines.AsNoTracking()
                join entry in db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                where line.BusinessId == businessId
                    && entry.BusinessId == businessId
                    && entry.Status == EntryStatus.Posted
                    && accountIds.Contains(line.AccountId)
                    && (rangeFrom == null || entry.EntryDate >= rangeFrom)
                    && (rangeTo == null || entry.EntryDate <= rangeTo)
                group line by line.AccountId
                into grouped
                select new
                {
                    AccountId = grouped.Key,
                    Debit = grouped.Sum(line => line.DebitAmount),
                    Credit = grouped.Sum(line => line.CreditAmount),
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return activity.ToDictionary(item => item.AccountId, item => (item.Debit, item.Credit));
    }

    private static IResult InvalidRange()
        => ApiResults.ValidationProblem(
            "The date range is invalid.",
            new Dictionary<string, string[]> { ["from"] = ["The start date must not be after the end date."] });

    private static IResult ReportResponse(string? format, object report, string fileName)
    {
        if (string.IsNullOrWhiteSpace(format) || string.Equals(format.Trim(), "json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(report);
        }

        if (string.Equals(format.Trim(), "csv", StringComparison.OrdinalIgnoreCase))
        {
            return CsvReportWriter.Create(report, fileName);
        }

        return ApiResults.ValidationProblem(
            "The report format is invalid.",
            new Dictionary<string, string[]> { ["format"] = ["Use 'json' or 'csv'."] });
    }

    private sealed record AccountActivity(Guid AccountId, decimal Debit, decimal Credit);

    private sealed record TaxFact(decimal TaxRate, string TaxMode, decimal Amount);
}
