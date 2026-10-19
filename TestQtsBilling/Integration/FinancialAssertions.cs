using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace TestQtsBilling.Integration;

/// <summary>
/// The financial assertions of the fixture (specification task 01).
/// </summary>
/// <remarks>
/// Every assertion compares exact decimals, and every check is scoped to one business, one entry or
/// one document. A global cancellation elsewhere can never make a failing entry pass: the entry
/// balance is checked per entry, and the document total is checked against its own lines.
/// </remarks>
public static class FinancialAssertions
{
    /// <summary>Reads the committed state of the financial facts of a business.</summary>
    public static async Task<FinancialState> CaptureAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken = default)
    {
        var journal = await db.JournalLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId)
            .Select(line => new { line.JournalEntryId, line.DebitAmount, line.CreditAmount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var stock = await db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == businessId)
            .Select(balance => new { balance.VariantId, balance.LocationId, balance.OnHand })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var payments = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId)
            .Select(payment => new { payment.Amount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var audit = await db.AuditEvents.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId)
            .Select(entry => new { entry.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new FinancialState(
            journal.Sum(line => line.DebitAmount),
            journal.Sum(line => line.CreditAmount),
            stock.Sum(balance => balance.OnHand),
            payments.Sum(payment => payment.Amount),
            audit.Count,
            journal.Select(line => line.JournalEntryId).Distinct().Count());
    }

    /// <summary>
    /// Asserts that every posted entry of the business balances on its own. An entry that is out of
    /// balance fails the assertion even when the business as a whole happens to cancel out.
    /// </summary>
    public static async Task AssertEveryEntryBalancesAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken = default)
    {
        var entries = await db.JournalLines.AsNoTracking()
            .Where(line => line.BusinessId == businessId)
            .GroupBy(line => line.JournalEntryId)
            .Select(group => new
            {
                EntryId = group.Key,
                Debit = group.Sum(line => line.DebitAmount),
                Credit = group.Sum(line => line.CreditAmount),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        AssertEntriesBalance(entries.Select(entry => (entry.EntryId, entry.Debit, entry.Credit)));
    }

    /// <summary>
    /// The pure core of the entry check: every entry balances on its own. A compensating entry
    /// somewhere else can never make a broken entry pass.
    /// </summary>
    public static void AssertEntriesBalance(IEnumerable<(Guid EntryId, decimal Debit, decimal Credit)> entries)
    {
        var unbalanced = entries
            .Where(entry => entry.Debit != entry.Credit)
            .Select(entry => $"{entry.EntryId}: debits {entry.Debit}, credits {entry.Credit}")
            .ToArray();

        Assert.True(
            unbalanced.Length == 0,
            $"A posted journal entry does not balance: {string.Join("; ", unbalanced)}");
    }

    /// <summary>Asserts that the header totals of a sale equal the sums of its own lines.</summary>
    public static async Task AssertSaleMatchesItsLinesAsync(BillingDbContext db, Guid saleId, CancellationToken cancellationToken = default)
    {
        var sale = await db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == saleId, cancellationToken)
            .ConfigureAwait(false);

        Assert.NotNull(sale);

        var lines = await db.SaleLines.AsNoTracking()
            .Where(line => line.SaleId == saleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.NotEmpty(lines);

        var lineTotal = lines.Sum(line => line.LineTotal);
        var tax = lines.Sum(line => line.TaxAmount);

        Assert.Equal(lineTotal, sale!.TotalAmount);
        Assert.Equal(tax, sale.TaxAmount);

        // The revenue base of every line is the payable minus the tax inside it, and the sum of the
        // revenue bases is the revenue the journal credits (specification D03).
        var revenueBase = lines.Sum(line => line.LineTotal - line.TaxAmount);

        Assert.Equal(sale.TotalAmount - sale.TaxAmount, revenueBase);
    }

    /// <summary>Asserts that the state of a business did not change between two captures.</summary>
    public static void AssertUnchanged(FinancialState before, FinancialState after)
    {
        Assert.Equal(before.TotalDebits, after.TotalDebits);
        Assert.Equal(before.TotalCredits, after.TotalCredits);
        Assert.Equal(before.TotalStockOnHand, after.TotalStockOnHand);
        Assert.Equal(before.TotalPayments, after.TotalPayments);
        Assert.Equal(before.AuditEventCount, after.AuditEventCount);
        Assert.Equal(before.JournalEntryCount, after.JournalEntryCount);
    }
}

/// <summary>A snapshot of the financial facts of a business.</summary>
public sealed record FinancialState(
    decimal TotalDebits,
    decimal TotalCredits,
    decimal TotalStockOnHand,
    decimal TotalPayments,
    int AuditEventCount,
    int JournalEntryCount);
