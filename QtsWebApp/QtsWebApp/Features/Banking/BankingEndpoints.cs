using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
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
    private const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapBankingEndpoints(this IEndpointRouteBuilder app)
    {
        var banking = app.MapGroup("/api/v1/banking").WithTags("Banking");
        banking.MapGet("/accounts", ListAccountsAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/accounts", CreateAccountAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/accounts/{id:guid}/statements", ImportStatementAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapGet("/statements/{id:guid}", GetStatementAsync).RequireAuthorization(Permissions.AccountingPost);
        banking.MapPost("/statement-lines/{lineId:guid}/match", MatchLineAsync).RequireAuthorization(Permissions.AccountingPost);
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
                var line = await db.BankStatementLines
                    .FromSql($"SELECT * FROM bank_statement_lines WHERE id = {lineId} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) throw NotFoundLineException();
                if (line.MatchedJournalLineId is { } existingJournal)
                {
                    if (existingJournal != request.JournalLineId)
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict,
                            "This statement line has already been reconciled.", "A reconciled bank statement line cannot be rematched.");
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var import = await db.BankStatementImports.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == line.StatementImportId, cancellationToken)
                    .ConfigureAwait(false) ?? throw NotFoundLineException();
                var account = await db.BankAccounts.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == import.BankAccountId && row.IsActive, cancellationToken)
                    .ConfigureAwait(false) ?? throw NotFoundAccountException();
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

                var now = DateTimeOffset.UtcNow;
                line.MatchedJournalLineId = journalLine.Id;
                line.ReconciledAtUtc = now;
                line.ReconciledByUserId = userId;
                db.AuditEvents.Add(AuditEventFor(businessId, userId, line.Id, "matched", now, http.TraceIdentifier,
                    new { import.BankAccountId, importId = import.Id, journalLine.Id, line.Amount, line.ValueDate }));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var dto = await ToLineDtoAsync(db, businessId, lineId, cancellationToken).ConfigureAwait(false);
            return dto is null ? NotFoundLine() : Results.Ok(dto);
        }
        catch (DomainRuleException exception) { return exception.ToResult(); }
        catch (PostgresException exception) { return MapPostgresException(exception); }
    }

    private static async Task<BankStatementDetailDto?> ToDetailAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
    {
        var import = await db.BankStatementImports.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.Id == id)
            .Select(row => new BankStatementImportDto(row.Id, row.BankAccountId, row.FileName, row.StatementDate,
                row.OpeningBalance, row.ClosingBalance, row.LineCount, row.ImportedAtUtc))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (import is null) return null;
        var lines = await db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StatementImportId == id)
            .OrderBy(row => row.LineNo)
            .Select(row => new BankStatementLineDto(row.Id, row.LineNo, row.ValueDate, row.Description,
                row.Reference, row.Amount, row.MatchedJournalLineId, row.ReconciledAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new BankStatementDetailDto(import, lines);
    }

    private static Task<BankStatementLineDto?> ToLineDtoAsync(BillingDbContext db, Guid businessId, Guid id, CancellationToken cancellationToken)
        => db.BankStatementLines.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.Id == id)
            .Select(row => new BankStatementLineDto(row.Id, row.LineNo, row.ValueDate, row.Description,
                row.Reference, row.Amount, row.MatchedJournalLineId, row.ReconciledAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

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

    private static AuditEvent AuditEventFor(Guid businessId, Guid userId, Guid entityId, string action, DateTimeOffset now, string correlationId, object summary)
        => new()
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = "bank_statement_import",
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
