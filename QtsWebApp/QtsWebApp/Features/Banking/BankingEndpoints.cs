using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Banking;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Banking;

/// <summary>Bank statement import and journal-line reconciliation, scoped to the active business.</summary>
public static class BankingEndpoints
{
    private const int MaxCsvBytes = 5 * 1024 * 1024;
    private const int MaxStatementLines = 20_000;
    private const int MaxReasonLength = 500;
    private const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapBankingEndpoints(this IEndpointRouteBuilder app)
    {
        var banking = app.MapGroup("/api/v1/banking").WithTags("Banking");
        banking.MapGet("/accounts", ListAccountsAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/accounts", CreateAccountAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/accounts/{id:guid}/statements", ImportStatementAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapGet("/statements/{id:guid}", GetStatementAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapGet("/accounts/{id:guid}/statements", ListStatementsAsync).RequireAuthorization(Permissions.ReportsView);
        banking.MapGet("/statements/{id:guid}/review", GetReviewAsync).RequireAuthorization(Permissions.ReportsView);
        banking.MapPost("/statement-lines/{lineId:guid}/match", MatchLineAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/statement-lines/{lineId:guid}/unmatch", UnmatchLineAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/statement-lines/{lineId:guid}/movement", RecordMovementAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/statements/{id:guid}/finalise", FinaliseStatementAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/statements/{id:guid}/reopen", ReopenStatementAsync).RequireAuthorization(Permissions.AccountingClose);
        return app;
    }

    private static async Task<IResult> ListAccountsAsync(ClaimsPrincipal principal, BillingDbContext db, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();
        var rows = await (
            from account in db.BankAccounts.AsNoTracking()
            join ledger in db.Accounts.AsNoTracking() on new { account.LedgerAccountId, account.BusinessId } equals new { LedgerAccountId = ledger.Id, ledger.BusinessId }
            where account.BusinessId == businessId
            orderby account.Name
            select new { account, LedgerCode = ledger.Code, LedgerName = ledger.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows.Select(row => ToBankAccountDto(row.account, row.LedgerCode, row.LedgerName)).ToList());
    }

    private static async Task<IResult> CreateAccountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CreateBankAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();
        var errors = ValidateBankAccount(request);
        if (errors.Count > 0) return ApiResults.ValidationProblem("The bank-account payload is invalid.", errors);
        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
        if (business is null) return BusinessScope.MissingMembership();
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (!string.Equals(currency, business.BaseCurrency, StringComparison.OrdinalIgnoreCase))
            return ApiResults.ValidationProblem("The bank-account currency is invalid.",
                new Dictionary<string, string[]> { ["currency"] = [$"Bank statement reconciliation currently requires the business currency ({business.BaseCurrency})."] });
        var ledger = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(account => account.BusinessId == businessId && account.Id == request.LedgerAccountId
                && account.IsActive && account.AccountType == AccountType.Asset, cancellationToken)
            .ConfigureAwait(false);
        if (ledger is null)
            return ApiResults.ValidationProblem("The bank-account payload is invalid.",
                new Dictionary<string, string[]> { ["ledgerAccountId"] = ["Choose an active asset account in this business."] });

        var account = new BankAccount
        {
            BusinessId = businessId,
            Name = request.Name.Trim(),
            AccountNumberMasked = request.AccountNumberMasked.Trim(),
            Iban = NormalizeIban(request.Iban),
            Currency = currency,
            LedgerAccountId = ledger.Id,
            IsActive = true,
        };
        db.BankAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The bank account name or ledger account is already registered.");
        }
        return Results.Created($"/api/v1/banking/accounts/{account.Id:D}", ToBankAccountDto(account, ledger.Code, ledger.Name));
    }

    private static async Task<IResult> ImportStatementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpRequest http,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        if (key.Length is 0 or > 64)
            return ApiResults.ValidationProblem("An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters."] });
        if (!http.HasFormContentType)
            return ApiResults.Problem(StatusCodes.Status415UnsupportedMediaType, ApiErrorCodes.ValidationFailed,
                "A multipart form with a CSV file is required.");

        IFormCollection form;
        try { form = await http.ReadFormAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException) { return ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "The statement form is invalid."); }
        var file = form.Files.GetFile("file");
        var fileName = SafeFileName(file?.FileName);
        var errors = new Dictionary<string, string[]>();
        if (file is null || file.Length is 0 or > MaxCsvBytes) errors["file"] = [$"Provide a CSV file from 1 byte to {MaxCsvBytes} bytes."];
        if (fileName is null) errors["fileName"] = ["The file name must be between 1 and 200 characters."];
        if (!DateOnly.TryParseExact(form["statementDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var statementDate))
            errors["statementDate"] = ["Use the date format YYYY-MM-DD."];
        if (!TryFormAmount(form["openingBalance"], out var openingBalance)) errors["openingBalance"] = ["Opening balance must be a decimal with at most 4 places."];
        if (!TryFormAmount(form["closingBalance"], out var closingBalance)) errors["closingBalance"] = ["Closing balance must be a decimal with at most 4 places."];
        if (errors.Count > 0) return ApiResults.ValidationProblem("The bank-statement payload is invalid.", errors);

        var account = await db.BankAccounts.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id && row.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return NotFoundAccount();

        byte[] bytes;
        await using (var stream = new MemoryStream())
        {
            await file!.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            bytes = stream.ToArray();
        }
        IReadOnlyList<ParsedStatementLine> parsedLines;
        try { parsedLines = ParseCsv(bytes); }
        catch (FormatException exception)
        {
            return ApiResults.ValidationProblem("The bank-statement CSV is invalid.",
                new Dictionary<string, string[]> { ["file"] = [exception.Message] });
        }
        if (parsedLines.Count is 0 or > MaxStatementLines)
            return ApiResults.ValidationProblem("The bank-statement CSV is invalid.",
                new Dictionary<string, string[]> { ["file"] = [$"The statement must contain 1-{MaxStatementLines} transaction rows."] });

        var calculatedClosing = LedgerWriter.Round(openingBalance + parsedLines.Sum(line => line.Amount));
        if (calculatedClosing != LedgerWriter.Round(closingBalance))
            return ApiResults.ValidationProblem("The bank-statement balances do not reconcile.",
                new Dictionary<string, string[]> { ["closingBalance"] = [$"Opening balance plus transactions equals {calculatedClosing.ToString(CultureInfo.InvariantCulture)}, not the supplied closing balance."] });

        var fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fingerprint = RequestFingerprint.Compute(new { BankAccountId = id, StatementDate = statementDate,
            OpeningBalance = LedgerWriter.Round(openingBalance), ClosingBalance = LedgerWriter.Round(closingBalance), FileHash = fileHash });
        var existing = await db.BankStatementImports.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return await ReplayOrConflictAsync(db, businessId, existing, fingerprint, cancellationToken).ConfigureAwait(false);
        if (await db.BankStatementImports.AsNoTracking().AnyAsync(row => row.BusinessId == businessId && row.BankAccountId == id && row.FileHash == fileHash, cancellationToken).ConfigureAwait(false))
            return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.IdempotencyKeyReused,
                "This CSV file has already been imported for the bank account.");

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            var importId = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                var duplicate = await db.BankStatementImports.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                {
                    if (!string.Equals(duplicate.PayloadHash, fingerprint, StringComparison.Ordinal)) throw SameKeyDifferentPayload();
                    return duplicate.Id;
                }
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var lockedAccount = await db.BankAccounts
                    .FromSql($"SELECT * FROM bank_accounts WHERE id = {id} AND business_id = {businessId} FOR SHARE")
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (lockedAccount is null || !lockedAccount.IsActive) throw NotFoundAccountException();
                var import = new BankStatementImport
                {
                    BusinessId = businessId,
                    BankAccountId = id,
                    FileName = fileName!,
                    StatementDate = statementDate,
                    OpeningBalance = LedgerWriter.Round(openingBalance),
                    ClosingBalance = LedgerWriter.Round(closingBalance),
                    FileHash = fileHash,
                    IdempotencyKey = key,
                    PayloadHash = fingerprint,
                    LineCount = parsedLines.Count,
                    ImportedAtUtc = DateTimeOffset.UtcNow,
                    ImportedByUserId = userId,
                };
                db.BankStatementImports.Add(import);
                for (var index = 0; index < parsedLines.Count; index++)
                {
                    var parsed = parsedLines[index];
                    db.BankStatementLines.Add(new BankStatementLine
                    {
                        BusinessId = businessId,
                        StatementImportId = import.Id,
                        LineNo = index + 1,
                        ValueDate = parsed.ValueDate,
                        Description = parsed.Description,
                        Reference = parsed.Reference,
                        Amount = parsed.Amount,
                    });
                }
                db.AuditEvents.Add(AuditEventFor(businessId, userId, import.Id, "imported", import.ImportedAtUtc, context.TraceIdentifier,
                    new { import.FileName, import.StatementDate, import.LineCount, import.OpeningBalance, import.ClosingBalance, import.FileHash }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return import.Id;
            }).ConfigureAwait(false);
            var detail = await ToDetailAsync(db, businessId, importId, cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v1/banking/statements/{importId:D}", detail);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.ChangeTracker.Clear();
            var winner = await db.BankStatementImports.AsNoTracking()
                .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                .ConfigureAwait(false);
            return winner is null
                ? ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This statement was concurrently imported.")
                : await ReplayOrConflictAsync(db, businessId, winner, fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) { return MapPostgresException(exception); }
    }

    private static async Task<IResult> GetStatementAsync(ClaimsPrincipal principal, BillingDbContext db, Guid id, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();
        var detail = await ToDetailAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
        return detail is null ? NotFoundStatement() : Results.Ok(detail);
    }

    private static async Task<IResult> MatchLineAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid lineId,
        MatchStatementLineRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var context = await LockLineAsync(db, businessId, lineId, cancellationToken).ConfigureAwait(false);
                if (context is null) throw NotFoundLineException();
                var (import, line, account) = context.Value;

                if (import.Status == BankReconciliationStatus.Finalised)
                    throw FinalisedException(import);
                if (request.ExpectedVersion is { } expected && expected != line.Version)
                    throw StaleLineException(line.Version);

                var now = DateTimeOffset.UtcNow;
                if (line.MatchedJournalLineId is { } existingJournal)
                {
                    if (existingJournal != request.JournalLineId)
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "This statement line has already been reconciled.",
                            "A reconciled bank statement line is corrected with a reason first, so the match it replaces stays on the record.");
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var journalLine = await db.JournalLines.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == request.JournalLineId, cancellationToken)
                    .ConfigureAwait(false);
                var entry = journalLine is null ? null : await db.JournalEntries.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == journalLine.JournalEntryId, cancellationToken)
                    .ConfigureAwait(false);
                var journalAmount = journalLine is null ? 0m : LedgerWriter.Round(journalLine.DebitAmount - journalLine.CreditAmount);
                if (journalLine is null || entry?.Status != EntryStatus.Posted || journalLine.AccountId != account.LedgerAccountId
                    || journalLine.Currency != account.Currency || journalAmount != line.Amount || entry.EntryDate != line.ValueDate)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "The journal line does not match this bank statement line.",
                        "Choose a posted line for this bank ledger account with the same value date, currency, and signed amount.");

                // A statement line cannot be claimed twice: the same posted line matched to another line is refused
                // here as plainly as the database index refuses it, because two lines that explain one movement are
                // one movement reconciled twice (specification task 31, T31.02).
                if (await db.BankStatementLines.AsNoTracking().AnyAsync(
                        row => row.BusinessId == businessId && row.Id != line.Id && row.MatchedJournalLineId == journalLine.Id,
                        cancellationToken).ConfigureAwait(false))
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "That posted journal line is already reconciled to another statement line.",
                        "Choose a movement the statement has not claimed, or correct the match that holds it first.");

                line.MatchedJournalLineId = journalLine.Id;
                line.ReconciledAtUtc = now;
                line.ReconciledByUserId = userId;
                db.BankStatementReconciliationEvents.Add(EventFor(
                    businessId, import.Id, line.Id, account.Id, BankReconciliationAction.Matched, null, journalLine.Id, null,
                    line.Amount, null, now, userId,
                    // A match needs no command key: it is replay-safe by its own shape, and a line that was matched,
                    // corrected and matched again to the same entry must not be refused by a spent key.
                    string.Empty,
                    RequestFingerprint.Compute(new { lineId = line.Id, journalLineId = journalLine.Id, line.Amount })));
                db.AuditEvents.Add(AuditEventFor(businessId, userId, line.Id, "matched", now, http.TraceIdentifier,
                    new { import.BankAccountId, importId = import.Id, journalLine.Id, entry = entry!.EntryNumber, line.Amount, line.ValueDate }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var dto = await ToLineDtoAsync(db, businessId, lineId, cancellationToken).ConfigureAwait(false);
            return dto is null ? NotFoundLine() : Results.Ok(dto);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
    }

    /// <summary>
    /// The statements imported for one bank account, newest first, named by their file and statement date: an
    /// accountant opens a review by recognising a statement, never by typing the identifier it was stored under.
    /// </summary>
    private static async Task<IResult> ListStatementsAsync(
        ClaimsPrincipal principal, BillingDbContext db, Guid id, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();
        var account = await db.BankAccounts.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (account is null) return NotFoundAccount();

        var imports = await db.BankStatementImports.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.BankAccountId == id)
            .OrderByDescending(row => row.StatementDate)
            .ThenByDescending(row => row.ImportedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var actorIds = imports.SelectMany(row => new[] { row.ImportedByUserId }.Concat(row.FinalisedByUserId is { } actor ? [actor] : []))
            .Distinct().ToArray();
        var users = await UserNamesAsync(db, actorIds).ConfigureAwait(false);
        return Results.Ok(imports
            .Select(row => DetailFor(
                row, [],
                users.GetValueOrDefault(row.ImportedByUserId, "a previous user"),
                row.FinalisedByUserId is { } finaliser ? users.GetValueOrDefault(finaliser, "a previous user") : null).Import)
            .ToList());
    }

    /// <summary>
    /// The reviewer's whole position on one statement: the bank's own figures, what the booked bank account actually
    /// moved in the period the statement closes, the posted lines each unmatched movement could be matched to, and
    /// every decision already recorded. Nothing here is computed on the screen (specification task 31, recipe step 2).
    /// </summary>
    private static async Task<IResult> GetReviewAsync(
        ClaimsPrincipal principal, BillingDbContext db, Guid id, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId)) return BusinessScope.MissingMembership();
        var import = await db.BankStatementImports.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (import is null) return NotFoundStatement();

        var joined = await (from bankAccount in db.BankAccounts.AsNoTracking()
                            join ledger in db.Accounts.AsNoTracking()
                                on new { bankAccount.LedgerAccountId, bankAccount.BusinessId } equals new { LedgerAccountId = ledger.Id, ledger.BusinessId }
                            where bankAccount.BusinessId == businessId && bankAccount.Id == import.BankAccountId
                            select new { Account = bankAccount, Ledger = ledger }).AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (joined is null) return NotFoundAccount();
        var account = joined.Account;

        var business = await db.Businesses.AsNoTracking().SingleAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
        var settings = await LedgerWriter.ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        var lines = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StatementImportId == id)
            .OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);

        var period = await db.AccountingPeriods.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId
                && row.StartDate <= import.StatementDate && row.EndDate >= import.StatementDate, cancellationToken)
            .ConfigureAwait(false);
        var windowStart = period?.StartDate ?? new DateOnly(import.StatementDate.Year, 1, 1);

        // The booked-bank control: what the ledger says moved through this bank account in the period the statement
        // closes, and what it holds at the statement date. Read from posted lines only - a draft changes nothing.
        var booked = await (from bookedLine in db.JournalLines.AsNoTracking()
                            join bookedEntry in db.JournalEntries.AsNoTracking() on bookedLine.JournalEntryId equals bookedEntry.Id
                            where bookedLine.BusinessId == businessId && bookedLine.AccountId == account.LedgerAccountId
                                && bookedEntry.Status == EntryStatus.Posted && bookedEntry.EntryDate <= import.StatementDate
                            select new { bookedEntry.EntryDate, Net = bookedLine.DebitAmount - bookedLine.CreditAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var bookedMovement = LedgerWriter.Round(booked.Where(row => row.EntryDate >= windowStart).Sum(row => row.Net));
        var bookedBalance = LedgerWriter.Round(booked.Sum(row => row.Net));

        var matchedTotal = LedgerWriter.Round(lines.Where(row => row.MatchedJournalLineId is not null).Sum(row => row.Amount));
        var unmatched = lines.Where(row => row.MatchedJournalLineId is null).ToArray();
        var movementTotal = LedgerWriter.Round(lines.Sum(row => row.Amount));
        var calculatedClosing = LedgerWriter.Round(import.OpeningBalance + movementTotal);

        // The ledger side of this statement: the nets of the posted lines it is matched to. A matched line is only
        // trustworthy while the two sides agree, so any difference here is a data fault the reviewer must see rather
        // than a rounding footnote.
        var matchedJournalLineIds = lines
            .Where(row => row.MatchedJournalLineId is not null)
            .Select(row => row.MatchedJournalLineId!.Value)
            .Distinct()
            .ToArray();
        var ledgerSide = matchedJournalLineIds.Length == 0
            ? 0m
            : LedgerWriter.Round(await db.JournalLines.AsNoTracking()
                .Where(journalLine => journalLine.BusinessId == businessId && matchedJournalLineIds.Contains(journalLine.Id))
                .SumAsync(journalLine => journalLine.DebitAmount - journalLine.CreditAmount, cancellationToken).ConfigureAwait(false));
        var difference = LedgerWriter.Round(ledgerSide - matchedTotal);

        var claimed = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.MatchedJournalLineId != null)
            .Select(row => row.MatchedJournalLineId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false);
        var poolStart = lines.Count == 0 ? import.StatementDate : lines.Min(row => row.ValueDate).AddDays(-7);
        var poolEnd = lines.Count == 0 ? import.StatementDate : lines.Max(row => row.ValueDate).AddDays(7);
        var pool = await (from candidateLine in db.JournalLines.AsNoTracking()
                          join candidateEntry in db.JournalEntries.AsNoTracking() on candidateLine.JournalEntryId equals candidateEntry.Id
                          join candidateAccount in db.Accounts.AsNoTracking() on candidateLine.AccountId equals candidateAccount.Id
                          where candidateLine.BusinessId == businessId && candidateLine.AccountId == account.LedgerAccountId
                              && candidateEntry.Status == EntryStatus.Posted && candidateAccount.BusinessId == businessId
                              && !claimed.Contains(candidateLine.Id)
                              && candidateEntry.EntryDate >= poolStart && candidateEntry.EntryDate <= poolEnd
                          select new BankStatementCandidateDto(
                              candidateLine.Id, candidateEntry.Id, candidateEntry.EntryNumber, candidateEntry.EntryDate, candidateEntry.Memo,
                              LedgerWriter.Round(candidateLine.DebitAmount - candidateLine.CreditAmount), candidateAccount.Code, candidateAccount.Name))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var candidates = lines.Select(line => new BankStatementLineReviewDto(
            line.Id,
            line.LineNo,
            line.MatchedJournalLineId is not null
                ? []
                : pool.Where(candidate => candidate.Amount == line.Amount
                        && candidate.EntryDate >= line.ValueDate.AddDays(-7)
                        && candidate.EntryDate <= line.ValueDate.AddDays(7)
                        && (Math.Abs((candidate.EntryDate.ToDateTime(TimeOnly.MinValue) - line.ValueDate.ToDateTime(TimeOnly.MinValue)).TotalDays) <= 2
                            || (line.Reference is { Length: > 0 } reference
                                && candidate.Description is { Length: > 0 } memo
                                && memo.Contains(reference, StringComparison.OrdinalIgnoreCase))))
                    // The nearest movement first: an operator reading a fee line should not have to scan the week.
                    .OrderBy(candidate => Math.Abs((candidate.EntryDate.ToDateTime(TimeOnly.MinValue) - line.ValueDate.ToDateTime(TimeOnly.MinValue)).TotalDays))
                    .ThenBy(candidate => candidate.EntryNumber)
                    .Take(5)
                    .ToList())).ToList();

        var entryNumbers = await EntryNumbersAsync(db, businessId, lines, cancellationToken).ConfigureAwait(false);
        var lineDtos = lines.Select(line => new BankStatementLineDto(
            line.Id, line.LineNo, line.ValueDate, line.Description, line.Reference, line.Amount,
            line.MatchedJournalLineId, line.ReconciledAtUtc,
            line.MatchedJournalLineId is { } journalLine ? entryNumbers.GetValueOrDefault(journalLine) : null,
            line.Version)).ToList();

        var actorIds = new List<Guid> { import.ImportedByUserId };
        if (import.FinalisedByUserId is { } finaliser) actorIds.Add(finaliser);
        var users = await UserNamesAsync(db, actorIds).ConfigureAwait(false);
        var events = await EventsAsync(db, businessId, id, entryNumbers, users, cancellationToken).ConfigureAwait(false);
        var importedBy = users.GetValueOrDefault(import.ImportedByUserId, "a previous user");
        var finalisedBy = import.FinalisedByUserId is { } actor ? users.GetValueOrDefault(actor, "a previous user") : null;

        var arithmetic = new BankStatementArithmeticDto(
            import.OpeningBalance, movementTotal, calculatedClosing, import.ClosingBalance,
            matchedTotal, LedgerWriter.Round(unmatched.Sum(row => row.Amount)), unmatched.Length,
            ledgerSide, bookedMovement, bookedBalance, difference,
            calculatedClosing == LedgerWriter.Round(import.ClosingBalance),
            unmatched.Length == 0 && difference == 0m && calculatedClosing == LedgerWriter.Round(import.ClosingBalance),
            settings?.SettlementScale ?? 2,
            business.TimeZone);

        var detail = DetailFor(import, lineDtos, importedBy, finalisedBy);
        return Results.Ok(new BankStatementReviewDto(
            ToBankAccountDto(account, joined.Ledger.Code, joined.Ledger.Name), detail, arithmetic, candidates, events,
            period?.Name, period is not null && period.Status != AccountingPeriodStatus.Open));
    }

    /// <summary>
    /// A reasoned correction. The line is released from the journal it was matched to and the match it replaces is
    /// kept as evidence, so the ledger still reads as a history rather than a story. No posted journal is written,
    /// moved or undone by a correction - the business's own entry is not the bank's (task 31, T31.03).
    /// </summary>
    private static async Task<IResult> UnmatchLineAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid lineId,
        UnmatchStatementLineRequest request,
        HttpRequest http,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        if (KeyRefusal(http) is { } refusal) return refusal;
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        if (ReasonRefusal(request.Reason) is { } reasonRefusal) return reasonRefusal;
        var reason = request.Reason!.Trim();
        var fingerprint = RequestFingerprint.Compute(new { lineId, request.ExpectedLineVersion, request.ExpectedJournalLineId, reason });

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var replay = await db.BankStatementReconciliationEvents.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (replay is not null)
                {
                    if (!string.Equals(replay.PayloadHash, fingerprint, StringComparison.Ordinal)) throw SameKeyDifferentPayload();
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var locked = await LockLineAsync(db, businessId, lineId, cancellationToken).ConfigureAwait(false);
                if (locked is null) throw NotFoundLineException();
                var (import, line, account) = locked.Value;
                if (import.Status == BankReconciliationStatus.Finalised) throw FinalisedException(import);
                if (line.Version != request.ExpectedLineVersion) throw StaleLineException(line.Version);
                if (line.MatchedJournalLineId is not { } prior)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "That statement line is not matched to anything.",
                        "There is nothing to correct: match the line, or record the fee or interest it represents.");
                if (prior != request.ExpectedJournalLineId)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "That line is matched to a different entry than the one being corrected.",
                        "Read the statement again before correcting it; a match somebody else made is not overwritten silently.");

                var now = DateTimeOffset.UtcNow;
                line.MatchedJournalLineId = null;
                line.ReconciledAtUtc = null;
                line.ReconciledByUserId = null;
                db.BankStatementReconciliationEvents.Add(EventFor(
                    businessId, import.Id, line.Id, account.Id, BankReconciliationAction.Unmatched, prior, null, null,
                    line.Amount, reason, now, userId, key, fingerprint));
                db.AuditEvents.Add(AuditEventFor(businessId, userId, line.Id, "unmatched", now, context.TraceIdentifier,
                    new { importId = import.Id, priorJournalLineId = prior, reason }, entityType: "bank_statement_line"));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }

        var dto = await ToLineDtoAsync(db, businessId, lineId, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFoundLine() : Results.Ok(dto);
    }

    /// <summary>
    /// A fee or interest line the bank moved that the business has not booked. It is posted as an ordinary reviewed
    /// journal through the existing posting rules - the bank leg against this account's own ledger account, the other
    /// leg against an account the reviewer chose by name - and the statement line is then matched to the bank leg it
    /// produced. Nothing is written off into a difference account (task 31, recipe step 4).
    /// </summary>
    private static async Task<IResult> RecordMovementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid lineId,
        RecordStatementMovementRequest request,
        HttpRequest http,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        if (KeyRefusal(http) is { } refusal) return refusal;
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        if (ReasonRefusal(request.Reason) is { } reasonRefusal) return reasonRefusal;
        var reason = request.Reason!.Trim();
        var fingerprint = RequestFingerprint.Compute(new { lineId, request.AccountId, request.ExpectedLineVersion, request.PostingDate, reason });

        var strategy = db.Database.CreateExecutionStrategy();
        BankStatementImport? settledImport = null;
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var replay = await db.BankStatementReconciliationEvents.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (replay is not null)
                {
                    if (!string.Equals(replay.PayloadHash, fingerprint, StringComparison.Ordinal)) throw SameKeyDifferentPayload();
                    settledImport = await db.BankStatementImports.AsNoTracking()
                        .SingleAsync(row => row.BusinessId == businessId && row.Id == replay.StatementImportId, cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var locked = await LockLineAsync(db, businessId, lineId, cancellationToken).ConfigureAwait(false);
                if (locked is null) throw NotFoundLineException();
                var (import, line, account) = locked.Value;
                if (import.Status == BankReconciliationStatus.Finalised) throw FinalisedException(import);
                if (line.Version != request.ExpectedLineVersion) throw StaleLineException(line.Version);
                if (line.MatchedJournalLineId is not null)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "That statement line is already reconciled.",
                        "A movement is recorded for a line the bank moved and the business has not booked yet.");

                var counter = await db.Accounts.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == request.AccountId && row.IsActive,
                        cancellationToken).ConfigureAwait(false);
                var isCharge = line.Amount < 0m;
                var wanted = isCharge ? AccountType.Expense : AccountType.Revenue;
                if (counter is null || counter.AccountType != wanted)
                    throw new DomainRuleException(
                        StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed,
                        isCharge
                            ? "A bank charge needs an expense account to close against."
                            : "Bank interest needs a revenue account to close against.",
                        isCharge
                            ? "Choose an active expense account in this business - the bank's own ledger account is filled in by the server."
                            : "Choose an active revenue account in this business - the bank's own ledger account is filled in by the server.");

                var postingDate = request.PostingDate ?? line.ValueDate;
                var movement = LedgerWriter.Round(Math.Abs(line.Amount));
                var chosenAccountId = request.AccountId;
                var bankLeg = isCharge
                    ? new JournalLineRequest(account.LedgerAccountId, 0m, movement, line.Description, null)
                    : new JournalLineRequest(account.LedgerAccountId, movement, 0m, line.Description, null);
                var counterLeg = isCharge
                    ? new JournalLineRequest(chosenAccountId, movement, 0m, line.Description, null)
                    : new JournalLineRequest(chosenAccountId, 0m, movement, line.Description, null);

                // The posting rules, the period gate and the number series all belong to the ledger: a bank movement
                // is not allowed a private shortcut around them.
                var entry = await LedgerWriter.PostWithinTransactionAsync(
                    db, businessId, userId, account.Currency, postingDate,
                    $"{(isCharge ? "Bank fee" : "Bank interest")} - {line.Description}: {reason}",
                    JournalSourceType.Manual, line.Id, null, [bankLeg, counterLeg], cancellationToken)
                    .ConfigureAwait(false);

                var bookedBankLeg = await db.JournalLines
                    .FirstAsync(row => row.BusinessId == businessId && row.JournalEntryId == entry.Id
                        && row.AccountId == account.LedgerAccountId, cancellationToken)
                    .ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                line.MatchedJournalLineId = bookedBankLeg.Id;
                line.ReconciledAtUtc = now;
                line.ReconciledByUserId = userId;
                db.BankStatementReconciliationEvents.Add(EventFor(
                    businessId, import.Id, line.Id, account.Id,
                    isCharge ? BankReconciliationAction.FeePosted : BankReconciliationAction.InterestPosted,
                    null, bookedBankLeg.Id, entry.Id, line.Amount, reason, now, userId, key, fingerprint));
                db.AuditEvents.Add(AuditEventFor(businessId, userId, line.Id, isCharge ? "fee_posted" : "interest_posted",
                    now, context.TraceIdentifier,
                    new { importId = import.Id, entry = entry.EntryNumber, entryId = entry.Id, counterAccount = counter!.Code, amount = movement, reason },
                    entityType: "bank_statement_line"));
                settledImport = import;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }

        if (settledImport is null) return NotFoundStatement();
        return Results.Ok(await ToDetailAsync(db, businessId, settledImport.Id, cancellationToken).ConfigureAwait(false)!);
    }

    /// <summary>
    /// Closes the review of a statement. It is refused while any line is unmatched, while the bank's own figures do
    /// not add up, or while the booked bank account does not tie to what was reconciled - an exception is shown, not
    /// folded into a difference (task 31, T31.01).
    /// </summary>
    private static async Task<IResult> FinaliseStatementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        FinaliseStatementRequest request,
        HttpRequest http,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        if (KeyRefusal(http) is { } refusal) return refusal;
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        var fingerprint = RequestFingerprint.Compute(new { id, request.ExpectedVersion });

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var replay = await db.BankStatementReconciliationEvents.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (replay is not null)
                {
                    if (!string.Equals(replay.PayloadHash, fingerprint, StringComparison.Ordinal)) throw SameKeyDifferentPayload();
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var import = await LockImportAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                if (import is null) throw NotFoundStatementException();
                if (import.Status == BankReconciliationStatus.Finalised)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "This statement has already been finalised.",
                        "Reopen it with a reason if the review has to change.");
                if (import.Version != request.ExpectedVersion) throw StaleStatementException(import.Version);
                var exceptions = await ExceptionsAsync(db, businessId, import, cancellationToken).ConfigureAwait(false);
                if (exceptions.Count > 0)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.BankStatementNotReconciled,
                        "The statement cannot be finalised while exceptions remain.",
                        string.Join(' ', exceptions));

                var now = DateTimeOffset.UtcNow;
                import.Status = BankReconciliationStatus.Finalised;
                import.FinalisedAtUtc = now;
                import.FinalisedByUserId = userId;
                db.BankStatementReconciliationEvents.Add(EventFor(
                    businessId, import.Id, null, import.BankAccountId, BankReconciliationAction.Finalised, null, null, null,
                    import.ClosingBalance, null, now, userId, key, fingerprint));
                db.AuditEvents.Add(AuditEventFor(businessId, userId, import.Id, "finalised", now, context.TraceIdentifier,
                    new { import.FileName, import.StatementDate, import.OpeningBalance, import.ClosingBalance }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }

        return await GetStatementAsync(principal, db, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reopens a finalised statement for correction, with a reason and under the closing authority. It changes the
    /// review state and nothing else: a closed accounting period stays closed, so a correction is answered by new
    /// events rather than by reaching back into a locked ledger (task 31, recipe step 3).
    /// </summary>
    private static async Task<IResult> ReopenStatementAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        ReopenStatementRequest request,
        HttpRequest http,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId)) return BusinessScope.MissingMembership();
        if (KeyRefusal(http) is { } refusal) return refusal;
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        if (ReasonRefusal(request.Reason) is { } reasonRefusal) return reasonRefusal;
        var reason = request.Reason!.Trim();
        var fingerprint = RequestFingerprint.Compute(new { id, request.ExpectedVersion, reason });

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var replay = await db.BankStatementReconciliationEvents.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (replay is not null)
                {
                    if (!string.Equals(replay.PayloadHash, fingerprint, StringComparison.Ordinal)) throw SameKeyDifferentPayload();
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var import = await LockImportAsync(db, businessId, id, cancellationToken).ConfigureAwait(false);
                if (import is null) throw NotFoundStatementException();
                if (import.Status == BankReconciliationStatus.Open)
                    throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                        "This statement is not finalised.",
                        "There is nothing to reopen: the review is still open for correction.");
                if (import.Version != request.ExpectedVersion) throw StaleStatementException(import.Version);

                var now = DateTimeOffset.UtcNow;
                import.Status = BankReconciliationStatus.Open;
                import.FinalisedAtUtc = null;
                import.FinalisedByUserId = null;
                db.BankStatementReconciliationEvents.Add(EventFor(
                    businessId, import.Id, null, import.BankAccountId, BankReconciliationAction.Reopened, null, null, null,
                    import.ClosingBalance, reason, now, userId, key, fingerprint));
                db.AuditEvents.Add(AuditEventFor(businessId, userId, import.Id, "reopened", now, context.TraceIdentifier,
                    new { import.FileName, import.StatementDate, reason }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }

        return await GetStatementAsync(principal, db, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The statement as an operator reads it: its own figures, who imported and who finalised it, and each line with
    /// the entry it is matched to named rather than keyed.
    /// </summary>
    private static async Task<BankStatementDetailDto?> ToDetailAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
    {
        var import = await db.BankStatementImports.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (import is null) return null;
        var lines = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StatementImportId == id)
            .OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entryNumbers = await EntryNumbersAsync(db, businessId, lines, cancellationToken).ConfigureAwait(false);
        var actorIds = new List<Guid> { import.ImportedByUserId };
        if (import.FinalisedByUserId is { } finaliser) actorIds.Add(finaliser);
        var users = await UserNamesAsync(db, actorIds).ConfigureAwait(false);
        return DetailFor(import,
            lines.Select(row => LineDto(row, entryNumbers)).ToList(),
            users.GetValueOrDefault(import.ImportedByUserId, "a previous user"),
            import.FinalisedByUserId is { } actor ? users.GetValueOrDefault(actor, "a previous user") : null);
    }

    private static async Task<BankStatementLineDto?> ToLineDtoAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
    {
        var line = await db.BankStatementLines.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken).ConfigureAwait(false);
        if (line is null) return null;
        var entryNumbers = await EntryNumbersAsync(db, businessId, [line], cancellationToken).ConfigureAwait(false);
        return LineDto(line, entryNumbers);
    }

    private static BankStatementLineDto LineDto(BankStatementLine line, IReadOnlyDictionary<Guid, string> entryNumbers)
        => new(line.Id, line.LineNo, line.ValueDate, line.Description, line.Reference, line.Amount,
            line.MatchedJournalLineId, line.ReconciledAtUtc,
            line.MatchedJournalLineId is { } journalLine ? entryNumbers.GetValueOrDefault(journalLine) : null,
            line.Version);

    private static BankStatementDetailDto DetailFor(
        BankStatementImport import, IReadOnlyList<BankStatementLineDto> lines, string importedBy, string? finalisedBy)
        => new(
            new BankStatementImportDto(
                import.Id, import.BankAccountId, import.FileName, import.StatementDate, import.OpeningBalance,
                import.ClosingBalance, import.LineCount, import.ImportedAtUtc,
                import.Status == BankReconciliationStatus.Finalised ? BankReconciliationState.Finalised : BankReconciliationState.Open,
                import.Version, import.FinalisedAtUtc, finalisedBy, importedBy, import.FileHash),
            lines);

    /// <summary>Entry numbers for the journal lines a statement is matched to, so a match is checked by name (R07).</summary>
    private static async Task<Dictionary<Guid, string>> EntryNumbersAsync(
        BillingDbContext db, Guid businessId, IReadOnlyCollection<BankStatementLine> lines, CancellationToken cancellationToken)
    {
        var journalLineIds = lines
            .Where(row => row.MatchedJournalLineId is not null)
            .Select(row => row.MatchedJournalLineId!.Value)
            .Distinct()
            .ToArray();
        if (journalLineIds.Length == 0) return [];
        return await (from journalLine in db.JournalLines.AsNoTracking()
                      join entry in db.JournalEntries.AsNoTracking() on journalLine.JournalEntryId equals entry.Id
                      where journalLine.BusinessId == businessId && journalLineIds.Contains(journalLine.Id)
                      select new { journalLine.Id, entry.EntryNumber })
            .ToDictionaryAsync(row => row.Id, row => row.EntryNumber, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<Guid, string>> UserNamesAsync(BillingDbContext db, IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0) return [];
        var ids = userIds.Distinct().ToArray();
        return await db.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName)
            .ConfigureAwait(false);
    }

    /// <summary>The reconciliation history in the order it happened, with the entries on both sides of each act.</summary>
    private static async Task<IReadOnlyList<BankReconciliationEventDto>> EventsAsync(
        BillingDbContext db, Guid businessId, Guid importId,
        IReadOnlyDictionary<Guid, string> entryNumbers, IReadOnlyDictionary<Guid, string> users,
        CancellationToken cancellationToken)
    {
        var rows = await db.BankStatementReconciliationEvents.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StatementImportId == importId)
            .OrderBy(row => row.OccurredAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var lineNos = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StatementImportId == importId)
            .ToDictionaryAsync(row => row.Id, row => row.LineNo, cancellationToken).ConfigureAwait(false);
        var wanted = rows.SelectMany(row => new[] { row.PriorJournalLineId, row.NewJournalLineId })
            .OfType<Guid>()
            .Where(id => !entryNumbers.ContainsKey(id))
            .Distinct()
            .ToArray();
        var lookedUp = wanted.Length == 0
            ? []
            : await (from journalLine in db.JournalLines.AsNoTracking()
                     join entry in db.JournalEntries.AsNoTracking() on journalLine.JournalEntryId equals entry.Id
                     where journalLine.BusinessId == businessId && wanted.Contains(journalLine.Id)
                     select new { journalLine.Id, entry.EntryNumber })
                .ToDictionaryAsync(row => row.Id, row => row.EntryNumber, cancellationToken).ConfigureAwait(false);
        var unknownActors = rows.Where(row => !users.ContainsKey(row.ActorUserId)).Select(row => row.ActorUserId).Distinct().ToArray();
        var actors = unknownActors.Length == 0 ? [] : await UserNamesAsync(db, unknownActors).ConfigureAwait(false);

        return rows.Select(row => new BankReconciliationEventDto(
            row.Id,
            row.StatementLineId is { } lineId ? lineNos.GetValueOrDefault(lineId, 0) : 0,
            Kind(row.Action),
            row.Amount,
            row.Reason,
            row.PriorJournalLineId is { } prior ? NumberFor(prior) : null,
            row.NewJournalLineId is { } next ? NumberFor(next) : null,
            row.OccurredAtUtc,
            users.TryGetValue(row.ActorUserId, out var known) ? known
                : actors.TryGetValue(row.ActorUserId, out var late) ? late
                : "a previous user")).ToList();

        string? NumberFor(Guid journalLineId)
            => entryNumbers.TryGetValue(journalLineId, out var known) ? known
                : lookedUp.TryGetValue(journalLineId, out var late) ? late
                : null;
    }

    private static string Kind(BankReconciliationAction action) => action switch
    {
        BankReconciliationAction.Matched => "matched",
        BankReconciliationAction.Unmatched => "unmatched",
        BankReconciliationAction.FeePosted => "fee",
        BankReconciliationAction.InterestPosted => "interest",
        BankReconciliationAction.Finalised => "finalised",
        BankReconciliationAction.Reopened => "reopened",
        _ => action.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Locks the statement header. Every reconciliation command takes this lock first and its line second, so a
    /// correction and a finalisation cannot each act on a state the other has already changed.
    /// </summary>
    private static async Task<BankStatementImport?> LockImportAsync(
        BillingDbContext db, Guid businessId, Guid importId, CancellationToken cancellationToken)
        => await db.BankStatementImports
            .FromSql($"SELECT * FROM bank_statement_imports WHERE id = {importId} AND business_id = {businessId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static async Task<(BankStatementImport Import, BankStatementLine Line, BankAccount Account)?> LockLineAsync(
        BillingDbContext db, Guid businessId, Guid lineId, CancellationToken cancellationToken)
    {
        var importId = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.Id == lineId)
            .Select(row => (Guid?)row.StatementImportId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (importId is null) return null;

        var import = await LockImportAsync(db, businessId, importId.Value, cancellationToken).ConfigureAwait(false);
        if (import is null) return null;

        var line = await db.BankStatementLines
            .FromSql($"SELECT * FROM bank_statement_lines WHERE id = {lineId} AND business_id = {businessId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) return null;

        var account = await db.BankAccounts.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == import.BankAccountId && row.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return account is null ? null : (import, line, account);
    }

    /// <summary>
    /// Why a statement may not be finalised yet, in the figures a reviewer has to answer: lines the bank moved that
    /// nobody explained, and a booked bank account that does not tie to what was reconciled. An unexplained difference
    /// is never written off into a clearing account (task 31, recipe step 4).
    /// </summary>
    private static async Task<IReadOnlyList<string>> ExceptionsAsync(
        BillingDbContext db, Guid businessId, BankStatementImport import, CancellationToken cancellationToken)
    {
        var lines = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StatementImportId == import.Id)
            .OrderBy(row => row.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var reasons = new List<string>();

        var movementTotal = LedgerWriter.Round(lines.Sum(row => row.Amount));
        var calculatedClosing = LedgerWriter.Round(import.OpeningBalance + movementTotal);
        if (calculatedClosing != LedgerWriter.Round(import.ClosingBalance))
            reasons.Add($"The statement's own figures do not add up: {import.OpeningBalance.ToString(CultureInfo.InvariantCulture)} plus {movementTotal.ToString(CultureInfo.InvariantCulture)} is {calculatedClosing.ToString(CultureInfo.InvariantCulture)}, not the {import.ClosingBalance.ToString(CultureInfo.InvariantCulture)} the bank printed.");

        var unmatched = lines.Where(row => row.MatchedJournalLineId is null).ToArray();
        if (unmatched.Length > 0)
            reasons.Add($"{unmatched.Length} line(s) are still unmatched: {string.Join(", ", unmatched.Select(row => $"{row.LineNo} ({row.Amount.ToString(CultureInfo.InvariantCulture)})"))}. Match them, or record the fee or interest they are.");

        var matchedJournalIds = lines
            .Where(row => row.MatchedJournalLineId is not null)
            .Select(row => row.MatchedJournalLineId!.Value)
            .Distinct()
            .ToArray();
        var ledgerSide = matchedJournalIds.Length == 0
            ? 0m
            : LedgerWriter.Round(await db.JournalLines.AsNoTracking()
                .Where(journalLine => journalLine.BusinessId == businessId && matchedJournalIds.Contains(journalLine.Id))
                .SumAsync(journalLine => journalLine.DebitAmount - journalLine.CreditAmount, cancellationToken).ConfigureAwait(false));
        var matchedTotal = LedgerWriter.Round(lines.Where(row => row.MatchedJournalLineId is not null).Sum(row => row.Amount));
        var difference = LedgerWriter.Round(ledgerSide - matchedTotal);
        if (difference != 0m)
            reasons.Add($"The lines this statement is matched to carry {ledgerSide.ToString(CultureInfo.InvariantCulture)} in the ledger against {matchedTotal.ToString(CultureInfo.InvariantCulture)} on the statement, a difference of {difference.ToString(CultureInfo.InvariantCulture)}. A difference is answered by a line, not by a write-off.");

        return reasons;
    }

    private static BankStatementReconciliationEvent EventFor(
        Guid businessId, Guid importId, Guid? lineId, Guid bankAccountId, BankReconciliationAction action,
        Guid? priorJournalLineId, Guid? newJournalLineId, Guid? journalEntryId, decimal amount, string? reason,
        DateTimeOffset now, Guid userId, string idempotencyKey, string payloadHash)
        => new()
        {
            BusinessId = businessId,
            StatementImportId = importId,
            StatementLineId = lineId,
            BankAccountId = bankAccountId,
            Action = action,
            PriorJournalLineId = priorJournalLineId,
            NewJournalLineId = newJournalLineId,
            JournalEntryId = journalEntryId,
            Amount = LedgerWriter.Round(amount),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            OccurredAtUtc = now,
            ActorUserId = userId,
            IdempotencyKey = idempotencyKey,
            PayloadHash = payloadHash,
        };

    private static IResult? KeyRefusal(HttpRequest http)
    {
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        return key.Length is 0 or > 64
            ? ApiResults.ValidationProblem("An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters; a reconciliation command is replayed under the same key, not repeated."] })
            : null;
    }

    private static IResult? ReasonRefusal(string? reason)
        => string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength
            ? ApiResults.ValidationProblem("The correction needs a reason.",
                new Dictionary<string, string[]> { ["reason"] = [$"Give the reason an auditor will read, in 1 to {MaxReasonLength} characters."] })
            : null;

    private static DomainRuleException FinalisedException(BankStatementImport import)
        => new(StatusCodes.Status409Conflict, ApiErrorCodes.BankStatementFinalised,
            "This statement has been finalised and cannot be corrected as it stands.",
            $"{import.FileName} for {import.StatementDate:yyyy-MM-dd} was finalised; reopen the review with a reason before changing how it was reconciled.");

    private static DomainRuleException StaleLineException(long actualVersion)
        => new(StatusCodes.Status409Conflict, ApiErrorCodes.OptimisticConcurrencyFailure,
            "That statement line has already been reconciled by somebody else.",
            $"The line is at version {actualVersion}; read the statement again before the same intention is repeated.");

    private static DomainRuleException StaleStatementException(long actualVersion)
        => new(StatusCodes.Status409Conflict, ApiErrorCodes.OptimisticConcurrencyFailure,
            "This statement has changed since it was read.",
            $"The statement is at version {actualVersion}; read it back before finalising or reopening it again.");

    private static DomainRuleException NotFoundStatementException()
        => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound,
            "The bank statement does not exist in this business.", "Check the statement identifier.");

    private static async Task<IResult> ReplayOrConflictAsync(
        BillingDbContext db, Guid businessId, BankStatementImport existing, string fingerprint, CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.PayloadHash, fingerprint, StringComparison.Ordinal)) return SameKeyDifferentPayload().ToResult();
        var detail = await ToDetailAsync(db, businessId, existing.Id, cancellationToken).ConfigureAwait(false);
        return detail is null ? NotFoundStatement() : Results.Ok(detail);
    }

    private static IReadOnlyList<ParsedStatementLine> ParseCsv(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new FormatException("The CSV must be valid UTF-8."); }
        var records = CsvRecords(text);
        if (records.Count < 2) throw new FormatException("The CSV needs a header and at least one transaction row.");
        var header = records[0].Select(value => value.Trim().TrimStart('\uFEFF')).ToArray();
        var dateColumn = Array.FindIndex(header, value => string.Equals(value, "Date", StringComparison.OrdinalIgnoreCase));
        var descriptionColumn = Array.FindIndex(header, value => string.Equals(value, "Description", StringComparison.OrdinalIgnoreCase));
        var referenceColumn = Array.FindIndex(header, value => string.Equals(value, "Reference", StringComparison.OrdinalIgnoreCase));
        var amountColumn = Array.FindIndex(header, value => string.Equals(value, "Amount", StringComparison.OrdinalIgnoreCase));
        if (header.Length != 4 || dateColumn < 0 || descriptionColumn < 0 || referenceColumn < 0 || amountColumn < 0)
            throw new FormatException("Use exactly these CSV columns: Date, Description, Reference, Amount.");
        var lines = new List<ParsedStatementLine>(records.Count - 1);
        for (var index = 1; index < records.Count; index++)
        {
            var row = records[index];
            if (row.Count != header.Length) throw new FormatException($"CSV row {index + 1} has {row.Count} fields; expected {header.Length}.");
            if (!DateOnly.TryParseExact(row[dateColumn].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueDate))
                throw new FormatException($"CSV row {index + 1} has an invalid date; use YYYY-MM-DD.");
            if (!decimal.TryParse(row[amountColumn].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                || decimal.Round(amount, 4) != amount || amount == 0m)
                throw new FormatException($"CSV row {index + 1} has an invalid non-zero amount with at most 4 decimal places.");
            var description = row[descriptionColumn].Trim();
            var reference = row[referenceColumn].Trim();
            if (description.Length is 0 or > 500 || reference.Length > 128)
                throw new FormatException($"CSV row {index + 1} has a missing/long description or a reference longer than 128 characters.");
            lines.Add(new ParsedStatementLine(valueDate, description, reference.Length == 0 ? null : reference, amount));
        }
        return lines;
    }

    private static List<List<string>> CsvRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (quoted)
            {
                if (current == '"' && index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                else if (current == '"') quoted = false;
                else field.Append(current);
                continue;
            }
            if (current == '"' && field.Length == 0) { quoted = true; continue; }
            if (current == ',') { record.Add(field.ToString()); field.Clear(); continue; }
            if (current is '\r' or '\n')
            {
                if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                record.Add(field.ToString()); field.Clear();
                if (record.Any(value => value.Length > 0)) records.Add(record);
                record = new List<string>();
                continue;
            }
            field.Append(current);
        }
        if (quoted) throw new FormatException("The CSV contains an unterminated quoted field.");
        record.Add(field.ToString());
        if (record.Any(value => value.Length > 0)) records.Add(record);
        return records;
    }

    private static Dictionary<string, string[]> ValidateBankAccount(CreateBankAccountRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) return new Dictionary<string, string[]> { ["request"] = ["A bank-account payload is required."] };
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200) errors["name"] = ["Name is required and limited to 200 characters."];
        var masked = request.AccountNumberMasked?.Trim() ?? string.Empty;
        if (masked.Length is < 4 or > 32 || masked.Any(character => !(char.IsDigit(character) || character is '*' or 'x' or 'X' or '•' or '-' or ' ')))
            errors["accountNumberMasked"] = ["Provide a masked account number containing only digits, asterisks, x, bullets, spaces, or hyphens."];
        if (request.Currency?.Trim().Length != 3 || request.Currency.Trim().Any(character => !char.IsAsciiLetter(character)))
            errors["currency"] = ["Currency must be a three-letter ISO code."];
        if (request.LedgerAccountId == Guid.Empty) errors["ledgerAccountId"] = ["A ledger asset account is required."];
        var iban = NormalizeIban(request.Iban);
        if (iban is not null && (iban.Length is < 15 or > 34 || iban.Any(character => !char.IsAsciiLetterOrDigit(character))))
            errors["iban"] = ["IBAN must contain 15-34 letters and digits."];
        return errors;
    }

    private static bool TryFormAmount(string? text, out decimal amount)
        => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && decimal.Round(amount, 4) == amount;

    private static string? SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var safe = fileName.Replace('\\', '/').Split('/').Last().Trim();
        return safe.Length is > 0 and <= 200 ? safe : null;
    }

    private static string? NormalizeIban(string? iban)
        => string.IsNullOrWhiteSpace(iban) ? null : string.Concat(iban.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private static BankAccountDto ToBankAccountDto(BankAccount account, string ledgerCode, string ledgerName)
        => new(account.Id, account.Name, account.AccountNumberMasked, MaskIban(account.Iban), account.Currency,
            account.LedgerAccountId, ledgerCode, ledgerName, account.IsActive);

    private static string? MaskIban(string? iban)
        => string.IsNullOrWhiteSpace(iban) ? null : $"••••{iban[^4..]}";

    private static DomainRuleException SameKeyDifferentPayload()
        => new(StatusCodes.Status409Conflict, ApiErrorCodes.SameKeyDifferentPayload,
            "This idempotency key was used for a different statement import.", "Retry the original file and metadata or use a new key.");

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static AuditEvent AuditEventFor(Guid businessId, Guid userId, Guid entityId, string action, DateTimeOffset now, string correlationId, object summary, string entityType = "bank_statement_import")
        => new()
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
            Summary = System.Text.Json.JsonSerializer.Serialize(summary),
        };

    private static IResult NotFoundAccount()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The active bank account does not exist in this business.");
    private static DomainRuleException NotFoundAccountException()
        => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The active bank account does not exist in this business.", "Check the bank-account identifier.");
    private static IResult NotFoundStatement()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The bank statement does not exist in this business.");
    private static IResult NotFoundLine()
        => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The statement line does not exist in this business.");
    private static DomainRuleException NotFoundLineException()
        => new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The statement line does not exist in this business.", "Check the line identifier.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The bank reconciliation write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                "The bank reconciliation write violates an integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation => ApiResults.Problem(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
                "The bank reconciliation write references a record outside this business."),
            _ => ApiResults.Problem(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError,
                "The bank reconciliation operation failed."),
        };

    private sealed record ParsedStatementLine(DateOnly ValueDate, string Description, string? Reference, decimal Amount);
}
