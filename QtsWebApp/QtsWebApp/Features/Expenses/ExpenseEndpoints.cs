using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Expenses;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Expenses;

/// <summary>
/// Expense integrity (QB-12): every expense posts exactly one balanced ledger entry inside the idempotent
/// write path - expense-account debit against cash or bank credit - and corrections are append-only
/// reversals that mirror the original entry. A retried request returns the stored outcome instead of
/// posting twice, and a reversal can only happen once per expense (ALREADY_REVERSED).
/// </summary>
public static class ExpenseEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string DefaultExpenseAccountCode = "5000";
    private const string CashAccountCode = "1000";
    private const string BankAccountCode = "1010";
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 500;
    private const int MaxReferenceLength = 128;
    private const int MaxReasonLength = 500;
    private const int MaxNoteLength = 2_000;

    public static IEndpointRouteBuilder MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/api/v1/expense-categories").WithTags("Expenses");

        categories.MapGet("/", ListCategoriesAsync).RequireAuthorization(Permissions.ExpensesManage);
        categories.MapPost("/", CreateCategoryAsync).RequireAuthorization(Permissions.ExpensesManage);
        categories.MapGet("/{id:guid}", GetCategoryAsync).RequireAuthorization(Permissions.ExpensesManage);
        categories.MapPut("/{id:guid}", UpdateCategoryAsync).RequireAuthorization(Permissions.ExpensesManage);
        categories.MapDelete("/{id:guid}", DeleteCategoryAsync).RequireAuthorization(Permissions.ExpensesManage);

        var expenses = app.MapGroup("/api/v1/expenses").WithTags("Expenses");

        expenses.MapGet("/", ListExpensesAsync).RequireAuthorization(Permissions.ExpensesManage);
        expenses.MapPost("/", CreateExpenseAsync).RequireAuthorization(Permissions.ExpensesManage);
        expenses.MapGet("/{id:guid}", GetExpenseAsync).RequireAuthorization(Permissions.ExpensesManage);
        expenses.MapPost("/{id:guid}/reverse", ReverseExpenseAsync).RequireAuthorization(Permissions.ExpensesManage);

        return app;
    }

    // ---------------------------------------------------------------- categories

    private static async Task<IResult> ListCategoriesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var query = db.ExpenseCategories.AsNoTracking().Where(category => category.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(category => category.IsActive);
        }

        var rows = await query
            .OrderBy(category => category.Name)
            .Select(category => new ExpenseCategoryDto(category.Id, category.Name, category.AccountId, category.IsActive))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateCategoryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveExpenseCategoryRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (request.AccountId is { } accountId && !await AccountExistsAsync(db, businessId, accountId, cancellationToken).ConfigureAwait(false))
        {
            errors["accountId"] = ["The account does not exist or is inactive in this business."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The expense category payload is invalid.", errors);
        }

        var duplicate = await db.ExpenseCategories.AsNoTracking()
            .AnyAsync(category => category.BusinessId == businessId && category.Name == name, cancellationToken)
            .ConfigureAwait(false);

        if (duplicate)
        {
            return DuplicateName();
        }

        var category = new ExpenseCategory
        {
            BusinessId = businessId,
            Name = name,
            AccountId = request.AccountId,
            IsActive = true,
        };

        db.ExpenseCategories.Add(category);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return DuplicateName();
        }

        return Results.Created($"/api/v1/expense-categories/{category.Id}", new ExpenseCategoryDto(category.Id, category.Name, category.AccountId, category.IsActive));
    }

    private static async Task<IResult> GetCategoryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var category = await db.ExpenseCategories.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return category is null
            ? NotFoundCategory()
            : Results.Ok(new ExpenseCategoryDto(category.Id, category.Name, category.AccountId, category.IsActive));
    }

    private static async Task<IResult> UpdateCategoryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        SaveExpenseCategoryRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var category = await db.ExpenseCategories
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (category is null)
        {
            return NotFoundCategory();
        }

        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (request.AccountId is { } accountId && !await AccountExistsAsync(db, businessId, accountId, cancellationToken).ConfigureAwait(false))
        {
            errors["accountId"] = ["The account does not exist or is inactive in this business."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The expense category payload is invalid.", errors);
        }

        var duplicate = await db.ExpenseCategories.AsNoTracking()
            .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id != id && candidate.Name == name, cancellationToken)
            .ConfigureAwait(false);

        if (duplicate)
        {
            return DuplicateName();
        }

        category.Name = name;
        category.AccountId = request.AccountId;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return DuplicateName();
        }

        return Results.Ok(new ExpenseCategoryDto(category.Id, category.Name, category.AccountId, category.IsActive));
    }

    private static async Task<IResult> DeleteCategoryAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var category = await db.ExpenseCategories
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (category is null)
        {
            return NotFoundCategory();
        }

        // Soft delete: posted expenses keep their category for reporting (D-29 pattern).
        category.IsActive = false;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- expenses

    private static async Task<IResult> ListExpensesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        Guid? categoryId,
        Guid? supplierId,
        string? search,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        var query = db.Expenses.AsNoTracking().Where(expense => expense.BusinessId == businessId);

        if (from is { } start)
        {
            query = query.Where(expense => expense.OccurredOn >= start);
        }

        if (to is { } end)
        {
            query = query.Where(expense => expense.OccurredOn <= end);
        }

        if (categoryId is { } category)
        {
            query = query.Where(expense => expense.ExpenseCategoryId == category);
        }

        if (supplierId is { } supplier)
        {
            query = query.Where(expense => expense.SupplierId == supplier);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(expense => EF.Functions.ILike(expense.Description, pattern));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(expense => expense.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(expense => expense.Id)
            .Take(size + 1)
            .Select(expense => new ExpenseRow(
                expense.Id,
                expense.ExpenseCategoryId,
                expense.LocationId,
                expense.SupplierId,
                expense.Description,
                expense.Amount,
                expense.Currency,
                expense.OccurredOn,
                expense.PaidVia,
                expense.Reference,
                expense.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var page = await ToDtosAsync(db, businessId, rows, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new PagedResult<ExpenseDto>(page, nextCursor, size));
    }

    private static async Task<IResult> GetExpenseAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var expense = await db.Expenses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (expense is null)
        {
            return NotFoundExpense();
        }

        var dto = await ToDtoAsync(db, businessId, ToRow(expense), cancellationToken).ConfigureAwait(false);

        return Results.Ok(dto);
    }

    private static async Task<IResult> CreateExpenseAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveExpenseRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();

        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var payloadHash = RequestFingerprint.Compute(request);

        var existing = await db.Expenses.AsNoTracking()
            .FirstOrDefaultAsync(expense => expense.BusinessId == businessId && expense.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different payload.",
                    $"Key '{idempotencyKey}' already recorded an expense; start a new request instead of changing the payload.");
            }

            return Results.Ok(await ToDtoAsync(db, businessId, ToRow(existing), cancellationToken).ConfigureAwait(false));
        }

        var business = await db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return BusinessScope.MissingMembership();
        }

        var errors = new Dictionary<string, string[]>();

        var description = request?.Description?.Trim() ?? string.Empty;

        if (description.Length is 0 or > MaxDescriptionLength)
        {
            errors["description"] = [$"A description of 1-{MaxDescriptionLength} characters is required."];
        }

        if (request is null || request.Amount <= 0 || LedgerWriter.Round(request.Amount) != request.Amount)
        {
            errors["amount"] = ["A positive amount of at most 4 decimal places is required."];
        }

        if (request?.OccurredOn == default)
        {
            errors["occurredOn"] = ["The expense date is required."];
        }

        if (!TryMapPaidVia(request?.PaidVia, out var paidVia, out var creditAccountCode))
        {
            errors["paidVia"] = ["Use cash, cheque, bankTransfer or card. Customer credit is not a way to pay an expense."];
        }

        if (request?.Reference is { Length: > MaxReferenceLength })
        {
            errors["reference"] = [$"A reference of at most {MaxReferenceLength} characters is supported."];
        }

        ExpenseCategory? category = null;

        if (request?.ExpenseCategoryId is { } categoryId)
        {
            category = await db.ExpenseCategories.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == categoryId, cancellationToken)
                .ConfigureAwait(false);

            if (category is null || !category.IsActive)
            {
                errors["expenseCategoryId"] = ["The expense category does not exist or is inactive in this business."];
            }
        }

        if (request?.LocationId is { } locationId)
        {
            var locationExists = await db.BusinessLocations.AsNoTracking()
                .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id == locationId && candidate.IsActive, cancellationToken)
                .ConfigureAwait(false);

            if (!locationExists)
            {
                errors["locationId"] = ["The location does not exist or is inactive in this business."];
            }
        }

        if (request?.SupplierId is { } supplierId)
        {
            var supplierExists = await db.Suppliers.AsNoTracking()
                .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id == supplierId && candidate.IsActive, cancellationToken)
                .ConfigureAwait(false);

            if (!supplierExists)
            {
                errors["supplierId"] = ["The supplier does not exist or is inactive in this business."];
            }
        }

        var expenseAccountCode = DefaultExpenseAccountCode;
        Account? expenseAccount = null;

        if (category?.AccountId is { } accountId)
        {
            expenseAccount = await db.Accounts.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == accountId && candidate.IsActive, cancellationToken)
                .ConfigureAwait(false);

            if (expenseAccount is null)
            {
                errors["expenseCategoryId"] = ["The account linked to this category does not exist or is inactive in this business."];
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The expense payload is invalid.", errors);
        }

        request = request!;
        var amount = LedgerWriter.Round(request.Amount);
        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var expense = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var accounts = await LedgerWriter.ResolveAccountsAsync(
                        db,
                        businessId,
                        [creditAccountCode],
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!accounts.TryGetValue(creditAccountCode, out var creditAccount))
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        $"Account {creditAccountCode} is missing from the chart of accounts.",
                        "Seed the standard chart of accounts before posting business documents.");
                }

                var debitAccount = expenseAccount ?? await db.Accounts.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Code == expenseAccountCode && candidate.IsActive, cancellationToken)
                    .ConfigureAwait(false);

                if (debitAccount is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        $"Account {expenseAccountCode} is missing from the chart of accounts.",
                        "Seed the standard chart of accounts before posting business documents.");
                }

                var expense = new Expense
                {
                    BusinessId = businessId,
                    ExpenseCategoryId = request.ExpenseCategoryId,
                    LocationId = request.LocationId,
                    SupplierId = request.SupplierId,
                    Description = description,
                    Amount = amount,
                    Currency = business.BaseCurrency,
                    OccurredOn = request.OccurredOn,
                    PaidVia = paidVia,
                    Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
                    CreatedByUserId = userId,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                };

                db.Expenses.Add(expense);

                var entry = await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        business.BaseCurrency,
                        request.OccurredOn,
                        $"Expense: {description}",
                        JournalSourceType.Expense,
                        expense.Id,
                        reversalOfEntryId: null,
                        [
                            new JournalLineRequest(debitAccount.Id, amount, 0m, "Expense", request.LocationId),
                            new JournalLineRequest(creditAccount.Id, 0m, amount, $"Paid via {paidVia}", request.LocationId),
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "expense",
                    EntityId = expense.Id,
                    Action = "created",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        amount,
                        paidVia = paidVia.ToString(),
                        journalEntry = entry.EntryNumber,
                        categoryId = request.ExpenseCategoryId,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return expense;
            }).ConfigureAwait(false);

            var dto = await ToDtoAsync(db, businessId, ToRow(expense), cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/expenses/{expense.Id}", dto);
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<IResult> ReverseExpenseAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        ExpenseActionRequest request,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (!TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var idempotencyKey = http.Request.Headers[IdempotencyHeader].ToString().Trim();

        if (idempotencyKey.Length is 0 or > 64)
        {
            return ApiResults.ValidationProblem(
                "An idempotency key is required.",
                new Dictionary<string, string[]> { [IdempotencyHeader] = ["Send a client-generated key of 1-64 characters in the Idempotency-Key header."] });
        }

        var reason = request?.Reason?.Trim() ?? string.Empty;

        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem(
                "The reversal payload is invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"A reason of 1-{MaxReasonLength} characters is required (audited with the actor and time)."] });
        }

        if (request?.Note is { Length: > MaxNoteLength })
        {
            return ApiResults.ValidationProblem(
                "The reversal payload is invalid.",
                new Dictionary<string, string[]> { ["note"] = [$"A note of at most {MaxNoteLength} characters is supported."] });
        }

        var payloadHash = RequestFingerprint.Compute(request);

        var existing = await db.ExpenseReversals.AsNoTracking()
            .FirstOrDefaultAsync(reversal => reversal.BusinessId == businessId && reversal.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different payload.",
                    $"Key '{idempotencyKey}' already recorded a reversal; start a new request instead of changing the payload.");
            }

            if (existing.ExpenseId != id)
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.SameKeyDifferentPayload,
                    "This idempotency key was used for a different expense.",
                    "Use a fresh key for each expense you reverse.");
            }

            var replayed = await db.Expenses.AsNoTracking()
                .FirstAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(await ToDtoAsync(db, businessId, ToRow(replayed), cancellationToken).ConfigureAwait(false));
        }

        var now = DateTimeOffset.UtcNow;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var expense = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var expense = await db.Expenses
                    .FromSql($"SELECT * FROM expenses WHERE id = {id} AND business_id = {businessId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (expense is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status404NotFound,
                        ApiErrorCodes.NotFound,
                        "The expense does not exist in this business.",
                        "Check the identifier: expenses are scoped to the signed-in business.");
                }

                var alreadyReversed = await db.ExpenseReversals.AsNoTracking()
                    .AnyAsync(reversal => reversal.BusinessId == businessId && reversal.ExpenseId == id, cancellationToken)
                    .ConfigureAwait(false);

                if (alreadyReversed)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.AlreadyReversed,
                        "This expense is already reversed.",
                        "Reversal is a one-time transition; the original entry and its mirror both stay in the ledger.");
                }

                var original = await db.JournalEntries.AsNoTracking()
                    .FirstOrDefaultAsync(entry => entry.BusinessId == businessId
                        && entry.SourceType == JournalSourceType.Expense
                        && entry.SourceId == id, cancellationToken)
                    .ConfigureAwait(false);

                if (original is null)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.Conflict,
                        "The expense has no posted journal entry to reverse.",
                        "Every expense must have a source journal entry; this is a data-integrity error to investigate.");
                }

                var originalLines = await db.JournalLines.AsNoTracking()
                    .Where(line => line.BusinessId == businessId && line.JournalEntryId == original.Id)
                    .OrderBy(line => line.LineNo)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var reversalLines = originalLines
                    .Select(line => new JournalLineRequest(line.AccountId, line.CreditAmount, line.DebitAmount, line.Description, line.LocationId))
                    .ToList();

                var reversalEntry = await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        expense.Currency,
                        expense.OccurredOn,
                        $"Expense reversal: {expense.Description} ({reason})",
                        JournalSourceType.Reversal,
                        original.Id,
                        reversalOfEntryId: original.Id,
                        reversalLines,
                        cancellationToken)
                    .ConfigureAwait(false);

                db.ExpenseReversals.Add(new ExpenseReversal
                {
                    BusinessId = businessId,
                    ExpenseId = expense.Id,
                    Reason = reason,
                    Note = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim(),
                    JournalEntryId = reversalEntry.Id,
                    ReversedAtUtc = now,
                    ReversedByUserId = userId,
                    IdempotencyKey = idempotencyKey,
                    PayloadHash = payloadHash,
                });

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "expense",
                    EntityId = expense.Id,
                    Action = "reversed",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = JsonSerializer.Serialize(new
                    {
                        reason,
                        reversalEntry = reversalEntry.EntryNumber,
                        originalEntry = original.EntryNumber,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return expense;
            }).ConfigureAwait(false);

            return Results.Ok(await ToDtoAsync(db, businessId, ToRow(expense), cancellationToken).ConfigureAwait(false));
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    // ---------------------------------------------------------------- shared helpers

    private static async Task<bool> AccountExistsAsync(
        BillingDbContext db,
        Guid businessId,
        Guid accountId,
        CancellationToken cancellationToken)
        => await db.Accounts.AsNoTracking()
            .AnyAsync(account => account.BusinessId == businessId && account.Id == accountId && account.IsActive, cancellationToken)
            .ConfigureAwait(false);

    private static bool TryMapPaidVia(string? value, out PaymentMethod method, out string creditAccountCode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "cash":
                method = PaymentMethod.Cash;
                creditAccountCode = CashAccountCode;
                return true;
            case "cheque":
                method = PaymentMethod.Cheque;
                creditAccountCode = BankAccountCode;
                return true;
            case "banktransfer":
                method = PaymentMethod.BankTransfer;
                creditAccountCode = BankAccountCode;
                return true;
            case "card":
                // Paying a supplier by card settles through the bank account; this is a treasury fact,
                // not a card-acquisition flow (which stays disabled per D-36).
                method = PaymentMethod.Card;
                creditAccountCode = BankAccountCode;
                return true;
            default:
                method = PaymentMethod.Cash;
                creditAccountCode = CashAccountCode;
                return false;
        }
    }

    private static ExpenseRow ToRow(Expense expense)
        => new(
            expense.Id,
            expense.ExpenseCategoryId,
            expense.LocationId,
            expense.SupplierId,
            expense.Description,
            expense.Amount,
            expense.Currency,
            expense.OccurredOn,
            expense.PaidVia,
            expense.Reference,
            expense.CreatedAtUtc);

    private static async Task<List<ExpenseDto>> ToDtosAsync(
        BillingDbContext db,
        Guid businessId,
        List<ExpenseRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var expenseIds = rows.Select(row => row.Id).ToArray();

        var categoryIds = rows
            .Where(row => row.ExpenseCategoryId is not null)
            .Select(row => row.ExpenseCategoryId!.Value)
            .Distinct()
            .ToArray();

        var categories = await db.ExpenseCategories.AsNoTracking()
            .Where(category => category.BusinessId == businessId && categoryIds.Contains(category.Id))
            .Select(category => new { category.Id, category.Name })
            .ToDictionaryAsync(category => category.Id, category => category.Name, cancellationToken)
            .ConfigureAwait(false);

        var entries = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId
                && entry.SourceType == JournalSourceType.Expense
                && entry.SourceId != null
                && expenseIds.Contains(entry.SourceId!.Value))
            .Select(entry => new { SourceId = entry.SourceId!.Value, entry.Id, entry.EntryNumber })
            .ToDictionaryAsync(entry => entry.SourceId, entry => entry, cancellationToken)
            .ConfigureAwait(false);

        var reversals = await db.ExpenseReversals.AsNoTracking()
            .Where(reversal => reversal.BusinessId == businessId && expenseIds.Contains(reversal.ExpenseId))
            .Select(reversal => new { reversal.ExpenseId, reversal.ReversedAtUtc, reversal.JournalEntryId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var reversalEntryIds = reversals.Select(reversal => reversal.JournalEntryId).Distinct().ToArray();

        var reversalEntries = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && reversalEntryIds.Contains(entry.Id))
            .Select(entry => new { entry.Id, entry.EntryNumber })
            .ToDictionaryAsync(entry => entry.Id, entry => entry.EntryNumber, cancellationToken)
            .ConfigureAwait(false);

        var reversalsByExpense = reversals.ToDictionary(reversal => reversal.ExpenseId);

        var dtos = new List<ExpenseDto>(rows.Count);

        foreach (var row in rows)
        {
            categories.TryGetValue(row.ExpenseCategoryId ?? Guid.Empty, out var categoryName);
            entries.TryGetValue(row.Id, out var entry);
            reversalsByExpense.TryGetValue(row.Id, out var reversal);

            string? reversalEntryNumber = null;

            if (reversal is not null)
            {
                reversalEntries.TryGetValue(reversal.JournalEntryId, out reversalEntryNumber);
            }

            dtos.Add(new ExpenseDto(
                row.Id,
                row.ExpenseCategoryId,
                categoryName,
                row.LocationId,
                row.SupplierId,
                row.Description,
                row.Amount,
                row.Currency,
                row.OccurredOn,
                row.PaidVia.ToString(),
                row.Reference,
                entry?.Id,
                entry?.EntryNumber,
                reversal?.ReversedAtUtc,
                reversal?.JournalEntryId,
                reversalEntryNumber,
                row.CreatedAtUtc));
        }

        return dtos;
    }

    private static async Task<ExpenseDto> ToDtoAsync(
        BillingDbContext db,
        Guid businessId,
        ExpenseRow row,
        CancellationToken cancellationToken)
    {
        var dtos = await ToDtosAsync(db, businessId, [row], cancellationToken).ConfigureAwait(false);

        return dtos[0];
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult DuplicateName()
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.DuplicateName,
            "This name is already used in this business.",
            "Names are unique per business; reuse the existing record or pick another name.");

    private static IResult NotFoundCategory()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The expense category does not exist in this business.");

    private static IResult NotFoundExpense()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The expense does not exist in this business.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_expenses_business_idempotency_key" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "A concurrent request with the same key committed first.",
                    "Retry the same payload: the stored expense will be returned instead of posting twice."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.DuplicateName,
                    "The expense write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The expense write violates an integrity guard."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The expense write references a record outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The expense operation failed."),
        };

    private sealed record ExpenseRow(
        Guid Id,
        Guid? ExpenseCategoryId,
        Guid? LocationId,
        Guid? SupplierId,
        string Description,
        decimal Amount,
        string Currency,
        DateOnly OccurredOn,
        PaymentMethod PaidVia,
        string? Reference,
        DateTimeOffset CreatedAtUtc);
}
