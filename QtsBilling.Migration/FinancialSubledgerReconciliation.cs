using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsBilling.Migration;

/// <summary>Zero-tolerance checks between the authoritative open-item/stock facts and posted ledger control balances.</summary>
public static class FinancialSubledgerReconciliation
{
    public static async Task<IReadOnlyList<FinancialReconciliationCheck>> CheckAsync(
        BillingDbContext db,
        IReadOnlyList<Guid> businessIds,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<FinancialReconciliationCheck>(businessIds.Count * 3);
        foreach (var businessId in businessIds.Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await db.BusinessAccountingSettings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.BusinessId == businessId, cancellationToken)
                .ConfigureAwait(false);
            if (settings is null)
            {
                checks.Add(MissingSettings("accounts_receivable", businessId));
                checks.Add(MissingSettings("accounts_payable", businessId));
                checks.Add(MissingSettings("inventory_asset", businessId));
                continue;
            }

            var receivables = await db.Invoices.AsNoTracking()
                .Where(invoice => invoice.BusinessId == businessId
                    && (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.PartiallyPaid))
                .SumAsync(invoice => (decimal?)invoice.BalanceDue, cancellationToken)
                .ConfigureAwait(false) ?? 0m;
            var receivableLedger = await LedgerBalanceAsync(
                db, businessId, settings.AccountsReceivableAccountId, creditNormal: false, cancellationToken)
                .ConfigureAwait(false);
            checks.Add(Create("accounts_receivable", businessId, receivables, receivableLedger));

            var payables = await db.SupplierBills.AsNoTracking()
                .Where(bill => bill.BusinessId == businessId
                    && (bill.Status == SupplierBillStatus.Issued || bill.Status == SupplierBillStatus.PartiallyPaid))
                .SumAsync(bill => (decimal?)bill.BalanceDue, cancellationToken)
                .ConfigureAwait(false) ?? 0m;
            var payableLedger = await LedgerBalanceAsync(
                db, businessId, settings.AccountsPayableAccountId, creditNormal: true, cancellationToken)
                .ConfigureAwait(false);
            checks.Add(Create("accounts_payable", businessId, payables, payableLedger));

            var inventory = await db.InventoryBalances.AsNoTracking()
                .Where(balance => balance.BusinessId == businessId)
                .SumAsync(balance => (decimal?)balance.ValueOnHand, cancellationToken)
                .ConfigureAwait(false) ?? 0m;
            var inventoryLedger = await LedgerBalanceAsync(
                db, businessId, settings.InventoryAssetAccountId, creditNormal: false, cancellationToken)
                .ConfigureAwait(false);
            checks.Add(Create("inventory_asset", businessId, inventory, inventoryLedger));
        }

        return checks;
    }

    private static async Task<decimal> LedgerBalanceAsync(
        BillingDbContext db,
        Guid businessId,
        Guid accountId,
        bool creditNormal,
        CancellationToken cancellationToken)
    {
        var totals = await (from line in db.JournalLines.AsNoTracking()
                            join entry in db.JournalEntries.AsNoTracking() on new { line.JournalEntryId, line.BusinessId } equals new { JournalEntryId = entry.Id, entry.BusinessId }
                            where line.BusinessId == businessId
                                && line.AccountId == accountId
                                && entry.Status == EntryStatus.Posted
                            group line by line.AccountId into grouped
                            select new
                            {
                                Debit = grouped.Sum(line => line.DebitAmount),
                                Credit = grouped.Sum(line => line.CreditAmount),
                            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return Round(creditNormal
            ? (totals?.Credit ?? 0m) - (totals?.Debit ?? 0m)
            : (totals?.Debit ?? 0m) - (totals?.Credit ?? 0m));
    }

    private static FinancialReconciliationCheck Create(string name, Guid businessId, decimal subledger, decimal ledger)
    {
        var subledgerTotal = Round(subledger);
        var ledgerBalance = Round(ledger);
        var variance = Round(subledgerTotal - ledgerBalance);
        return new FinancialReconciliationCheck(name, businessId, subledgerTotal, ledgerBalance, variance, variance == 0m, null);
    }

    private static FinancialReconciliationCheck MissingSettings(string name, Guid businessId)
        => new(name, businessId, null, null, null, false, "business accounting settings are missing");

    private static decimal Round(decimal amount) => decimal.Round(amount, 4, MidpointRounding.AwayFromZero);
}

public sealed record FinancialReconciliationCheck(
    string Name,
    Guid BusinessId,
    decimal? SubledgerTotal,
    decimal? LedgerBalance,
    decimal? Variance,
    bool Matches,
    string? Problem);
