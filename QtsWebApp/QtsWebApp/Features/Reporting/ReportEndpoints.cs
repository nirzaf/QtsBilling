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

        reports.MapGet("/balance-sheet", BalanceSheetAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/profit-and-loss", ProfitAndLossAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/stock-summary", StockSummaryAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/cash-summary", CashSummaryAsync).RequireAuthorization(Permissions.ReportsView);
        reports.MapGet("/sales-summary", SalesSummaryAsync).RequireAuthorization(Permissions.ReportsView);

        return app;
    }

    // ---------------------------------------------------------------- balance sheet

    private static async Task<IResult> BalanceSheetAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? asOf,
        string? currency,
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

        return Results.Ok(new BalanceSheetDto(
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
            IncludesProvisional: false));
    }

    // ---------------------------------------------------------------- profit and loss

    private static async Task<IResult> ProfitAndLossAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? currency,
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

        return Results.Ok(new ProfitAndLossDto(
            businessId,
            rangeFrom,
            rangeTo,
            business!.BaseCurrency,
            revenue.OrderBy(account => account.Code).ToList(),
            expenses.OrderBy(account => account.Code).ToList(),
            totalRevenue,
            totalExpenses,
            LedgerWriter.Round(totalRevenue - totalExpenses),
            IncludesProvisional: false));
    }

    // ---------------------------------------------------------------- stock summary

    private static async Task<IResult> StockSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? asOf,
        Guid? locationId,
        string? currency,
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

        return Results.Ok(new StockSummaryDto(
            businessId,
            cutOff,
            business!.BaseCurrency,
            rows,
            LedgerWriter.Round(rows.Sum(row => row.OnHand)),
            LedgerWriter.Round(rows.Sum(row => row.StockValue)),
            IncludesProvisional: false));
    }

    // ---------------------------------------------------------------- cash summary

    private static async Task<IResult> CashSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? currency,
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

        return Results.Ok(new CashSummaryDto(
            businessId,
            rangeFrom,
            rangeTo,
            business!.BaseCurrency,
            rows,
            LedgerWriter.Round(rows.Sum(row => row.OpeningBalance)),
            LedgerWriter.Round(rows.Sum(row => row.Debits)),
            LedgerWriter.Round(rows.Sum(row => row.Credits)),
            LedgerWriter.Round(rows.Sum(row => row.ClosingBalance)),
            IncludesProvisional: false));
    }

    // ---------------------------------------------------------------- sales summary

    private static async Task<IResult> SalesSummaryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        string? currency,
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

        return Results.Ok(new SalesSummaryDto(
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
            IncludesProvisional: false));
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

    private sealed record AccountActivity(Guid AccountId, decimal Debit, decimal Credit);
}
