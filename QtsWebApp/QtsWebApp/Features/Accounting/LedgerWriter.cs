using Microsoft.EntityFrameworkCore;
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
        CancellationToken cancellationToken)
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

        var period = await db.AccountingPeriods.AsNoTracking()
            .Where(candidate => candidate.BusinessId == businessId
                && candidate.StartDate <= entryDate
                && candidate.EndDate >= entryDate)
            .OrderByDescending(candidate => candidate.StartDate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (period is { Status: not AccountingPeriodStatus.Open })
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
            AccountingPeriodId = period?.Id,
            Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
            SourceType = sourceType,
            SourceId = sourceId,
            Status = EntryStatus.Posted,
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

        return await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId && wanted.Contains(account.Code))
            .ToDictionaryAsync(account => account.Code, cancellationToken)
            .ConfigureAwait(false);
    }

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
