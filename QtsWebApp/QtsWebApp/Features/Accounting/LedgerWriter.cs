using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Accounting;

/// <summary>
/// Writes posted journal entries. Callers own the transaction (accounting commands, sale posting and the
/// later purchase/expense flows), so one unit of work can combine the ledger with stock and business
/// documents while the period gate, source idempotency and number allocation stay in one place.
/// </summary>
internal static class LedgerWriter
{
    public const string JournalSequenceName = "journal";
    public const string JournalSequencePrefix = "JE-";

    private const int MoneyScale = 4;

    public static decimal Round(decimal amount) => decimal.Round(amount, MoneyScale, MidpointRounding.AwayFromZero);

    public static (decimal Debit, decimal Credit) Totals(IReadOnlyList<JournalLineRequest> lines)
        => (lines.Sum(line => Round(line.Debit)), lines.Sum(line => Round(line.Credit)));

    /// <summary>
    /// Inserts one posted entry inside the caller's transaction. The deferred balance trigger still fires at
    /// COMMIT, so an unbalanced entry cannot reach the database even if service validation regresses.
    /// </summary>
    public static async Task<JournalEntry> PostWithinTransactionAsync(
        BillingDbContext db,
        Guid businessId,
        Guid userId,
        string currency,
        DateOnly entryDate,
        string? memo,
        JournalSourceType sourceType,
        Guid? sourceId,
        Guid? reversalOfEntryId,
        IReadOnlyList<JournalLineRequest> lines,
        CancellationToken cancellationToken,
        bool allowClosedPeriod = false)
    {
        if (sourceId is { } reference)
        {
            var existing = await db.JournalEntries.AsNoTracking()
                .Where(entry => entry.BusinessId == businessId && entry.SourceType == sourceType && entry.SourceId == reference)
                .Select(entry => entry.EntryNumber)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                throw new DomainRuleException(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "The source reference was already posted.",
                    $"Business effect for this source already exists as {existing}; a retried delivery must not post twice.");
            }
        }

        AccountingPeriod? period;
        var lockStartedAt = Stopwatch.GetTimestamp();

        try
        {
            period = (await db.AccountingPeriods
                    .FromSql($"SELECT * FROM accounting_periods WHERE business_id = {businessId} AND start_date <= {entryDate} AND end_date >= {entryDate} FOR SHARE")
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .SingleOrDefault();
        }
        finally
        {
            BillingMetrics.RecordAccountingPeriodLockWait(Stopwatch.GetElapsedTime(lockStartedAt).TotalMilliseconds);
        }

        if (period is null)
        {
            // A posted fact without a period could never be closed or reported; fail closed instead of
            // silently writing a null period (specification task 10, finding F20).
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.AccountingPeriodMissing,
                "No accounting period covers the posting date.",
                $"No period covers {entryDate:yyyy-MM-dd}; create and open the period before posting.");
        }

        if (period.Status != AccountingPeriodStatus.Open
            && !(allowClosedPeriod && period.Status == AccountingPeriodStatus.Closed))
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.AccountingPeriodClosed,
                "The accounting period is closed.",
                $"'{period.Name}' ({period.StartDate:yyyy-MM-dd}..{period.EndDate:yyyy-MM-dd}) is {period.Status.ToString().ToLowerInvariant()}; reopen the period before posting.");
        }

        var entryNumber = await AllocateNumberAsync(
            db,
            businessId,
            JournalSequenceName,
            JournalSequencePrefix,
            entryDate.Year,
            cancellationToken).ConfigureAwait(false);

        var totals = Totals(lines);
        var now = DateTimeOffset.UtcNow;

        var entry = new JournalEntry
        {
            BusinessId = businessId,
            EntryNumber = entryNumber,
            EntryDate = entryDate,
            AccountingPeriodId = period.Id,
            Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
            SourceType = sourceType,
            SourceId = sourceId,
            Status = EntryStatus.Draft,
            Currency = currency,
            TotalDebit = totals.Debit,
            TotalCredit = totals.Credit,
            PostedAtUtc = now,
            PostedByUserId = userId,
            ReversalOfEntryId = reversalOfEntryId,
        };

        db.JournalEntries.Add(entry);

        var lineNo = 1;

        foreach (var line in lines)
        {
            db.JournalLines.Add(new JournalLine
            {
                BusinessId = businessId,
                JournalEntryId = entry.Id,
                LineNo = lineNo++,
                AccountId = line.AccountId,
                DebitAmount = Round(line.Debit),
                CreditAmount = Round(line.Credit),
                Currency = currency,
                Description = string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim(),
                LocationId = line.LocationId,
            });
        }

        // The database only accepts lines while the entry is draft, so the entry is persisted as a draft
        // first and then marked posted in the same caller-owned transaction. The deferred balance trigger
        // still evaluates the posted entry at COMMIT.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        entry.Status = EntryStatus.Posted;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry;
    }

    /// <summary>
    /// Transactional per-business/per-series counter with a row lock, so concurrent postings cannot produce
    /// duplicate numbers and a rolled-back posting consumes no number (spec §3.4 step 4, QB-11 reuses this).
    /// </summary>
    public static async Task<string> AllocateNumberAsync(
        BillingDbContext db,
        Guid businessId,
        string name,
        string prefix,
        int fiscalYear,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO invoice_sequences (id, business_id, name, prefix, fiscal_year, next_number, padding, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.CreateVersion7()}, {businessId}, {name}, {prefix}, {fiscalYear}, 1, 5, 1, now(), now())
            ON CONFLICT (business_id, name, fiscal_year) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        var sequence = await db.InvoiceSequences
            .FromSql($"SELECT * FROM invoice_sequences WHERE business_id = {businessId} AND name = {name} AND fiscal_year = {fiscalYear} FOR UPDATE")
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

        var number = sequence.NextNumber;
        sequence.NextNumber = number + 1;

        return $"{sequence.Prefix}{fiscalYear}-{number.ToString("D" + sequence.Padding.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>Loads the chart entries a posting needs by code; callers decide what a missing code means.</summary>
    public static async Task<Dictionary<string, Account>> ResolveAccountsAsync(
        BillingDbContext db,
        Guid businessId,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken)
    {
        var wanted = codes.ToArray();
        var settings = await ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == businessId && wanted.Contains(account.Code))
                .ToDictionaryAsync(account => account.Code, cancellationToken)
                .ConfigureAwait(false);
        }

        var configuredIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["1000"] = settings.CashAccountId,
            ["1010"] = settings.BankClearingAccountId,
            ["1100"] = settings.AccountsReceivableAccountId,
            ["1200"] = settings.InventoryAssetAccountId,
            ["1300"] = settings.InputTaxReceivableAccountId,
            ["2000"] = settings.AccountsPayableAccountId,
            ["2100"] = settings.OutputTaxPayableAccountId,
            ["2101"] = settings.GrniClearingAccountId,
            ["2105"] = settings.CustomerDepositsAccountId,
            ["3000"] = settings.RetainedEarningsAccountId,
            ["4000"] = settings.SalesRevenueAccountId,
            ["4010"] = settings.SalesReturnsAccountId,
            ["5000"] = settings.CogsAccountId,
            ["5150"] = settings.PurchasePriceVarianceAccountId,
            ["5200"] = settings.InventoryShrinkageAccountId,
            ["5300"] = settings.CashOverShortAccountId,
        };
        if (settings.RoundingAccountId is { } roundingAccountId)
        {
            configuredIds["5999"] = roundingAccountId;
        }
        var mappedIds = wanted.Where(configuredIds.ContainsKey).Select(code => configuredIds[code]).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId
                && (mappedIds.Contains(account.Id) || wanted.Contains(account.Code)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = accounts.ToDictionary(account => account.Id);
        var byCode = accounts.ToDictionary(account => account.Code, StringComparer.Ordinal);
        var result = new Dictionary<string, Account>(StringComparer.Ordinal);
        foreach (var code in wanted)
        {
            if (configuredIds.TryGetValue(code, out var accountId) && byId.TryGetValue(accountId, out var configuredAccount))
            {
                result[code] = configuredAccount;
            }
            else if (byCode.TryGetValue(code, out var defaultAccount))
            {
                result[code] = defaultAccount;
            }
        }

        return result;
    }

    public static Task<BusinessAccountingSettings?> ResolveSettingsAsync(
        BillingDbContext db,
        Guid businessId,
        CancellationToken cancellationToken)
        => db.BusinessAccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.BusinessId == businessId, cancellationToken);

    /// <summary>Business-local calendar date for period gating (falls back to UTC for unknown zones).</summary>
    public static DateOnly LocalDate(string? timeZone, DateTimeOffset instant)
    {
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return DateOnly.FromDateTime(instant.UtcDateTime);
    }
}
