using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Accounting;

/// <summary>
/// Balanced, immutable ledger (QB-07). Posted journal entries are never edited: corrections are reversal
/// entries, balances are always derived from posted lines, closed periods reject posting, and one idempotency
/// reference can produce at most one entry so a retried posting cannot double-post (F-03/F-04/F-11).
/// </summary>
public static class AccountingEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;
    private const int MaxCodeLength = 32;
    private const int MaxNameLength = 200;
    private const int MaxMemoLength = 2_000;
    private const int MoneyScale = 4;

    public static IEndpointRouteBuilder MapAccountingEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/v1/accounts").WithTags("Accounting");
        accounts.MapGet("/", ListAccountsAsync).RequireAuthorization(Permissions.ReportsView);
        accounts.MapPost("/", CreateAccountAsync).RequireAuthorization(Permissions.AccountingPost);
        accounts.MapPut("/{id:guid}", UpdateAccountAsync).RequireAuthorization(Permissions.AccountingPost);

        var settings = app.MapGroup("/api/v1/accounting/settings").WithTags("Accounting");
        settings.MapGet("/", GetBusinessAccountingSettingsAsync).RequireAuthorization(Permissions.ReportsView);
        settings.MapPut("/", SaveBusinessAccountingSettingsAsync).RequireAuthorization(Permissions.AccountingClose);

        var periods = app.MapGroup("/api/v1/accounting-periods").WithTags("Accounting");
        periods.MapGet("/", ListPeriodsAsync).RequireAuthorization(Permissions.ReportsView);
        periods.MapPost("/", CreatePeriodAsync).RequireAuthorization(Permissions.AccountingClose);
        periods.MapPost("/{id:guid}/close", ClosePeriodAsync).RequireAuthorization(Permissions.AccountingClose);

        app.MapPost("/api/v1/accounting/periods/{fiscalYear:int}/close-year", CloseFiscalYearAsync)
            .WithTags("Accounting")
            .RequireAuthorization(Permissions.AccountingClose);

        var journal = app.MapGroup("/api/v1/journal-entries").WithTags("Accounting");
        journal.MapGet("/", ListJournalAsync).RequireAuthorization(Permissions.ReportsView);
        journal.MapGet("/{id:guid}", GetJournalAsync).RequireAuthorization(Permissions.ReportsView);
        journal.MapPost("/", PostJournalAsync).RequireAuthorization(Permissions.AccountingPost);
        journal.MapPost("/{id:guid}/reverse", ReverseJournalAsync).RequireAuthorization(Permissions.AccountingPost);

        app.MapGet("/api/v1/reports/trial-balance", TrialBalanceAsync)
            .WithTags("Accounting")
            .RequireAuthorization(Permissions.ReportsView);

        return app;
    }

    private static async Task<IResult> GetBusinessAccountingSettingsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var settings = await db.BusinessAccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);
        return settings is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Accounting settings are not configured.")
            : Results.Ok(ToDto(settings));
    }

    private static async Task<IResult> SaveBusinessAccountingSettingsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveBusinessAccountingSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (request.SettlementScale is not (0 or 2 or 3 or 4))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.SettlementScale)] = ["Settlement scale must be 0, 2, 3, or 4."],
            });
        }

        var mapping = new (string Field, Guid AccountId, AccountType Type)[]
        {
            (nameof(request.CashAccountId), request.CashAccountId, AccountType.Asset),
            (nameof(request.BankClearingAccountId), request.BankClearingAccountId, AccountType.Asset),
            (nameof(request.AccountsReceivableAccountId), request.AccountsReceivableAccountId, AccountType.Asset),
            (nameof(request.InventoryAssetAccountId), request.InventoryAssetAccountId, AccountType.Asset),
            (nameof(request.InputTaxReceivableAccountId), request.InputTaxReceivableAccountId, AccountType.Asset),
            (nameof(request.AccountsPayableAccountId), request.AccountsPayableAccountId, AccountType.Liability),
            (nameof(request.OutputTaxPayableAccountId), request.OutputTaxPayableAccountId, AccountType.Liability),
            (nameof(request.GrniClearingAccountId), request.GrniClearingAccountId, AccountType.Liability),
            (nameof(request.CustomerDepositsAccountId), request.CustomerDepositsAccountId, AccountType.Liability),
            (nameof(request.RetainedEarningsAccountId), request.RetainedEarningsAccountId, AccountType.Equity),
            (nameof(request.SalesRevenueAccountId), request.SalesRevenueAccountId, AccountType.Revenue),
            (nameof(request.SalesReturnsAccountId), request.SalesReturnsAccountId, AccountType.Revenue),
            (nameof(request.CogsAccountId), request.CogsAccountId, AccountType.Expense),
            (nameof(request.PurchasePriceVarianceAccountId), request.PurchasePriceVarianceAccountId, AccountType.Expense),
            (nameof(request.InventoryShrinkageAccountId), request.InventoryShrinkageAccountId, AccountType.Expense),
            (nameof(request.CashOverShortAccountId), request.CashOverShortAccountId, AccountType.Expense),
        };
        var ids = mapping.Select(item => item.AccountId)
            .Concat(request.RoundingAccountId is { } roundingAccountId ? [roundingAccountId] : [])
            .Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId && ids.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, cancellationToken)
            .ConfigureAwait(false);
        var errors = new Dictionary<string, string[]>();
        foreach (var (field, accountId, type) in mapping)
        {
            if (!accounts.TryGetValue(accountId, out var account) || !account.IsActive)
            {
                errors[field] = ["Select an active account from this business."];
            }
            else if (account.AccountType != type)
            {
                errors[field] = [$"Select an account classified as {type}."];
            }
        }

        if (request.RoundingAccountId is { } configuredRoundingAccountId
            && (!accounts.TryGetValue(configuredRoundingAccountId, out var roundingAccount)
                || !roundingAccount.IsActive
                || roundingAccount.AccountType != AccountType.Expense))
        {
            errors[nameof(request.RoundingAccountId)] = ["Select an active expense account from this business."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var settings = await db.BusinessAccountingSettings
            .SingleOrDefaultAsync(row => row.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);
        if (settings is null)
        {
            settings = new BusinessAccountingSettings { BusinessId = businessId };
            db.BusinessAccountingSettings.Add(settings);
        }

        settings.SettlementScale = request.SettlementScale;
        settings.CashAccountId = request.CashAccountId;
        settings.BankClearingAccountId = request.BankClearingAccountId;
        settings.AccountsReceivableAccountId = request.AccountsReceivableAccountId;
        settings.InventoryAssetAccountId = request.InventoryAssetAccountId;
        settings.InputTaxReceivableAccountId = request.InputTaxReceivableAccountId;
        settings.AccountsPayableAccountId = request.AccountsPayableAccountId;
        settings.OutputTaxPayableAccountId = request.OutputTaxPayableAccountId;
        settings.GrniClearingAccountId = request.GrniClearingAccountId;
        settings.CustomerDepositsAccountId = request.CustomerDepositsAccountId;
        settings.RetainedEarningsAccountId = request.RetainedEarningsAccountId;
        settings.SalesRevenueAccountId = request.SalesRevenueAccountId;
        settings.SalesReturnsAccountId = request.SalesReturnsAccountId;
        settings.CogsAccountId = request.CogsAccountId;
        settings.PurchasePriceVarianceAccountId = request.PurchasePriceVarianceAccountId;
        settings.InventoryShrinkageAccountId = request.InventoryShrinkageAccountId;
        settings.CashOverShortAccountId = request.CashOverShortAccountId;
        settings.RoundingAccountId = request.RoundingAccountId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(settings));
    }

    private static BusinessAccountingSettingsDto ToDto(BusinessAccountingSettings settings) => new(
        settings.SettlementScale,
        settings.CashAccountId,
        settings.BankClearingAccountId,
        settings.AccountsReceivableAccountId,
        settings.InventoryAssetAccountId,
        settings.InputTaxReceivableAccountId,
        settings.AccountsPayableAccountId,
        settings.OutputTaxPayableAccountId,
        settings.GrniClearingAccountId,
        settings.CustomerDepositsAccountId,
        settings.RetainedEarningsAccountId,
        settings.SalesRevenueAccountId,
        settings.SalesReturnsAccountId,
        settings.CogsAccountId,
        settings.PurchasePriceVarianceAccountId,
        settings.InventoryShrinkageAccountId,
        settings.CashOverShortAccountId,
        settings.RoundingAccountId);

    // ---------------------------------------------------------------- accounts

    private static async Task<IResult> ListAccountsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? search,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var query = db.Accounts.AsNoTracking().Where(account => account.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(account => account.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(account =>
                EF.Functions.ILike(account.Code, pattern) || EF.Functions.ILike(account.Name, pattern));
        }

        var rows = await query
            .OrderBy(account => account.Code)
            .Select(account => new AccountRow(
                account.Id,
                account.Code,
                account.Name,
                account.AccountType,
                account.ParentId,
                account.IsActive,
                account.Version,
                account.CreatedAtUtc,
                account.UpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows
            .Select(row => new AccountDto(
                row.Id,
                row.Code,
                row.Name,
                EnumBridge.To<AccountKind>(row.AccountType),
                row.ParentId,
                row.IsActive,
                row.Version,
                row.CreatedAtUtc,
                row.UpdatedAtUtc))
            .ToList());
    }

    private static async Task<IResult> CreateAccountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var code = request.Code?.Trim() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>();

        if (code.Length is 0 or > MaxCodeLength)
        {
            errors["code"] = [$"A code of 1-{MaxCodeLength} characters is required."];
        }

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The account payload is invalid.", errors);
        }

        if (await db.Accounts.AnyAsync(
                account => account.BusinessId == businessId && account.Code == code,
                cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "An account with this code already exists in this business.");
        }

        if (request.ParentId is { } parentId)
        {
            var parentError = await ValidateParentAsync(db, businessId, parentId, null, cancellationToken).ConfigureAwait(false);

            if (parentError is not null)
            {
                return parentError;
            }
        }

        var account = new Account
        {
            BusinessId = businessId,
            Code = code,
            Name = name,
            AccountType = (AccountType)request.AccountType,
            ParentId = request.ParentId,
            IsActive = true,
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Created($"/api/v1/accounts/{account.Id}", ToDto(account));
    }

    private static async Task<IResult> UpdateAccountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        UpdateAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var account = await db.Accounts
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return NotFound();
        }

        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            return ApiResults.ValidationProblem(
                "The account payload is invalid.",
                new Dictionary<string, string[]> { ["name"] = [$"A name of 1-{MaxNameLength} characters is required."] });
        }

        if (request.ParentId is { } parentId)
        {
            var parentError = await ValidateParentAsync(db, businessId, parentId, id, cancellationToken).ConfigureAwait(false);

            if (parentError is not null)
            {
                return parentError;
            }
        }

        account.Name = name;
        account.ParentId = request.ParentId;
        account.IsActive = request.IsActive;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(account));
    }

    /// <summary>Guards hierarchy integrity: the parent must exist in the business and must not create a cycle.</summary>
    private static async Task<IResult?> ValidateParentAsync(
        BillingDbContext db,
        Guid businessId,
        Guid parentId,
        Guid? accountId,
        CancellationToken cancellationToken)
    {
        if (accountId == parentId)
        {
            return ApiResults.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "An account cannot be its own parent.");
        }

        var parents = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId)
            .Select(account => new { account.Id, account.ParentId })
            .ToDictionaryAsync(account => account.Id, account => account.ParentId, cancellationToken)
            .ConfigureAwait(false);

        if (!parents.ContainsKey(parentId))
        {
            return ApiResults.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The parent account does not exist in this business.");
        }

        if (accountId is { } current)
        {
            var seen = new HashSet<Guid>();
            Guid? cursor = parentId;

            while (cursor is { } step && seen.Add(step))
            {
                if (step == current)
                {
                    return ApiResults.Problem(
                        StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed,
                        "The parent account would create a hierarchy cycle.");
                }

                cursor = parents.TryGetValue(step, out var next) ? next : null;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- periods

    private static async Task<IResult> ListPeriodsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var rows = await db.AccountingPeriods.AsNoTracking()
            .Where(period => period.BusinessId == businessId)
            .OrderByDescending(period => period.StartDate)
            .Select(period => new PeriodRow(period.Id, period.Name, period.StartDate, period.EndDate, period.Status, period.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows
            .Select(row => new AccountingPeriodDto(
                row.Id,
                row.Name,
                row.StartDate,
                row.EndDate,
                EnumBridge.To<AccountingPeriodState>(row.Status),
                row.Version))
            .ToList());
    }

    private static async Task<IResult> CreatePeriodAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveAccountingPeriodRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > 100)
        {
            return ApiResults.ValidationProblem(
                "The period payload is invalid.",
                new Dictionary<string, string[]> { ["name"] = ["A name of 1-100 characters is required."] });
        }

        if (request.StartDate > request.EndDate)
        {
            return ApiResults.ValidationProblem(
                "The period payload is invalid.",
                new Dictionary<string, string[]> { ["startDate"] = ["The start date must not be after the end date."] });
        }

        var overlaps = await db.AccountingPeriods.AnyAsync(
            period => period.BusinessId == businessId
                && period.StartDate <= request.EndDate
                && period.EndDate >= request.StartDate,
            cancellationToken).ConfigureAwait(false);

        if (overlaps)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.AccountingPeriodOverlap,
                "The period overlaps an existing accounting period.");
        }

        var entity = new AccountingPeriod
        {
            BusinessId = businessId,
            Name = name,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Status = AccountingPeriodStatus.Open,
        };

        db.AccountingPeriods.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // Two concurrent creates can both pass the read above; the database range trigger is the
            // authority and refuses the loser instead of allowing two overlapping periods.
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.AccountingPeriodOverlap,
                "The period overlaps an existing accounting period.");
        }

        return Results.Created(
            $"/api/v1/accounting-periods/{entity.Id}",
            new AccountingPeriodDto(entity.Id, entity.Name, entity.StartDate, entity.EndDate, AccountingPeriodState.Open, entity.Version));
    }

    private static async Task<IResult> ClosePeriodAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        AccountingPeriod? period = null;
        var closed = false;

        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Closing takes the exclusive row lock that posting takes in share mode, so a posting either
            // completes before the close decision or observes the closed status and is refused.
            period = await db.AccountingPeriods
                .FromSql($"SELECT * FROM accounting_periods WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (period is null || period.Status != AccountingPeriodStatus.Open)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            period.Status = AccountingPeriodStatus.Closed;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            closed = true;
        }).ConfigureAwait(false);

        if (period is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "The accounting period does not exist in this business.");
        }

        if (!closed)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.AccountingPeriodClosed,
                "The accounting period is already closed.");
        }

        return Results.Ok(new AccountingPeriodDto(
            period.Id,
            period.Name,
            period.StartDate,
            period.EndDate,
            AccountingPeriodState.Closed,
            period.Version));
    }

    private static async Task<IResult> CloseFiscalYearAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        int fiscalYear,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }
        if (fiscalYear is < 1900 or > 9998)
        {
            return ApiResults.ValidationProblem("The fiscal year is invalid.", new Dictionary<string, string[]>
            {
                [nameof(fiscalYear)] = ["Enter a year from 1900 through 9998."],
            });
        }

        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is 0 or > 128)
        {
            return ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Send a client-generated key of 1-128 characters."],
            });
        }

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "accounting.close-year",
                key,
                new { fiscalYear },
                async () =>
                {
                    var startDate = new DateOnly(fiscalYear, 1, 1);
                    var endDate = new DateOnly(fiscalYear, 12, 31);
                    var periods = await db.AccountingPeriods
                        .FromSql($"SELECT * FROM accounting_periods WHERE business_id = {businessId} AND start_date <= {endDate} AND end_date >= {startDate} ORDER BY start_date FOR UPDATE")
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (periods.Count == 0)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.AccountingPeriodMissing, "No accounting periods cover this fiscal year.", $"Create closed periods covering {startDate:yyyy-MM-dd} through {endDate:yyyy-MM-dd} before closing the year.");
                    }
                    if (periods.Any(period => period.Status == AccountingPeriodStatus.Open))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.AccountingPeriodClosed, "Every accounting period in the fiscal year must be closed first.", "Close each open accounting period before running the year-end close.");
                    }
                    if (periods.Any(period => period.Status == AccountingPeriodStatus.Locked))
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This fiscal year has already been locked.", "The year-end close has already completed or the year requires finance review.");
                    }

                    var cursor = startDate;
                    foreach (var period in periods)
                    {
                        if (period.StartDate > cursor)
                        {
                            throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.AccountingPeriodMissing, "The accounting periods do not cover the complete fiscal year.", $"There is a period gap beginning {cursor:yyyy-MM-dd}.");
                        }
                        if (period.EndDate >= endDate)
                        {
                            cursor = endDate.AddDays(1);
                            break;
                        }
                        cursor = period.EndDate.AddDays(1);
                    }
                    if (cursor <= endDate)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.AccountingPeriodMissing, "The accounting periods do not cover the complete fiscal year.", $"The period coverage ends before {endDate:yyyy-MM-dd}.");
                    }

                    var closingPeriod = periods.SingleOrDefault(period => period.StartDate <= endDate && period.EndDate >= endDate);
                    if (closingPeriod is null || closingPeriod.Status != AccountingPeriodStatus.Closed)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.AccountingPeriodClosed, "The final accounting period must be closed before the year-end entry can be posted.", "Close the period containing December 31 before running the year-end close.");
                    }

                    var settings = await LedgerWriter.ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
                    var retainedEarnings = settings is null
                        ? await db.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.BusinessId == businessId && account.Code == "3000", cancellationToken).ConfigureAwait(false)
                        : await db.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.BusinessId == businessId && account.Id == settings.RetainedEarningsAccountId, cancellationToken).ConfigureAwait(false);
                    if (retainedEarnings is null || !retainedEarnings.IsActive || retainedEarnings.AccountType != AccountType.Equity)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The retained earnings account is unavailable.", "Configure an active equity account for retained earnings before closing the fiscal year.");
                    }

                    var balances = await (
                        from line in db.JournalLines.AsNoTracking()
                        join journalEntry in db.JournalEntries.AsNoTracking()
                            on new { line.JournalEntryId, line.BusinessId } equals new { JournalEntryId = journalEntry.Id, journalEntry.BusinessId }
                        join account in db.Accounts.AsNoTracking()
                            on new { line.AccountId, line.BusinessId } equals new { AccountId = account.Id, account.BusinessId }
                        where journalEntry.BusinessId == businessId
                            && journalEntry.Status == EntryStatus.Posted
                            && journalEntry.EntryDate >= startDate
                            && journalEntry.EntryDate <= endDate
                            && (account.AccountType == AccountType.Revenue || account.AccountType == AccountType.Expense)
                        group line by new { account.Id, account.Code, account.Name, account.AccountType } into accountLines
                        select new
                        {
                            accountLines.Key.Id,
                            accountLines.Key.Code,
                            accountLines.Key.Name,
                            accountLines.Key.AccountType,
                            NetDebit = accountLines.Sum(line => line.DebitAmount - line.CreditAmount),
                        })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    var lines = new List<JournalLineRequest>();
                    foreach (var balance in balances.Where(balance => balance.NetDebit != 0m).OrderBy(balance => balance.Code, StringComparer.Ordinal))
                    {
                        var amount = Math.Abs(balance.NetDebit);
                        lines.Add(balance.NetDebit > 0m
                            ? new JournalLineRequest(balance.Id, 0m, amount, $"Close {balance.Code} {balance.Name}", null)
                            : new JournalLineRequest(balance.Id, amount, 0m, $"Close {balance.Code} {balance.Name}", null));
                    }
                    if (lines.Count == 0)
                    {
                        throw new DomainRuleException(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "There are no revenue or expense balances to close.", "Review the trial balance and close the year only after financial activity has been posted.");
                    }

                    var debit = LedgerWriter.Totals(lines).Debit;
                    var credit = LedgerWriter.Totals(lines).Credit;
                    if (debit > credit)
                    {
                        lines.Add(new JournalLineRequest(retainedEarnings.Id, 0m, LedgerWriter.Round(debit - credit), "Transfer net income to retained earnings", null));
                    }
                    else if (credit > debit)
                    {
                        lines.Add(new JournalLineRequest(retainedEarnings.Id, LedgerWriter.Round(credit - debit), 0m, "Transfer net loss to retained earnings", null));
                    }

                    var entry = await LedgerWriter.PostWithinTransactionAsync(
                        db,
                        businessId,
                        userId,
                        (await db.Businesses.AsNoTracking().Where(item => item.Id == businessId).Select(item => item.BaseCurrency).SingleAsync(cancellationToken).ConfigureAwait(false)),
                        endDate,
                        $"Fiscal year {fiscalYear} closing entry",
                        JournalSourceType.FiscalYearClose,
                        Guid.CreateVersion7(),
                        null,
                        lines,
                        cancellationToken,
                        allowClosedPeriod: true).ConfigureAwait(false);

                    foreach (var period in periods)
                    {
                        period.Status = AccountingPeriodStatus.Locked;
                    }
                    db.AuditEvents.Add(new AuditEvent
                    {
                        BusinessId = businessId,
                        UserId = userId,
                        EntityType = "accounting_period",
                        EntityId = closingPeriod.Id,
                        Action = "fiscal_year_closed",
                        OccurredAtUtc = DateTimeOffset.UtcNow,
                        CorrelationId = http.TraceIdentifier,
                        Summary = System.Text.Json.JsonSerializer.Serialize(new { fiscalYear, entryNumber = entry.EntryNumber, lockedPeriodCount = periods.Count }),
                    });

                    var netIncome = LedgerWriter.Round(-balances.Sum(balance => balance.NetDebit));
                    return (StatusCodes.Status200OK, new FiscalYearCloseDto(fiscalYear, endDate, entry.EntryNumber, netIncome, periods.Count));
                },
                cancellationToken).ConfigureAwait(false);
            return Results.Json(execution.Body, statusCode: execution.StatusCode);
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
        catch (PostgresException exception)
        {
            return exception.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation
                ? ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The fiscal-year close conflicts with the current accounting state.", "Refresh the periods and trial balance before retrying.")
                : ApiResults.Problem(StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, "The fiscal-year close could not be completed.");
        }
    }

    // ---------------------------------------------------------------- journal

    private static async Task<IResult> ListJournalAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? cursor,
        int? pageSize,
        string? status,
        string? origin,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.JournalEntries.AsNoTracking().Where(entry => entry.BusinessId == businessId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<JournalEntryState>(status.Trim(), ignoreCase: true, out var wantedStatus))
            {
                return ApiResults.ValidationProblem(
                    "The filter is invalid.",
                    new Dictionary<string, string[]> { ["status"] = [$"Unknown status '{status}'."] });
            }

            var stored = EnumBridge.To<EntryStatus>(wantedStatus);
            query = query.Where(entry => entry.Status == stored);
        }

        if (!string.IsNullOrWhiteSpace(origin))
        {
            if (!Enum.TryParse<JournalOrigin>(origin.Trim(), ignoreCase: true, out var wantedOrigin))
            {
                return ApiResults.ValidationProblem(
                    "The filter is invalid.",
                    new Dictionary<string, string[]> { ["origin"] = [$"Unknown origin '{origin}'."] });
            }

            var stored = EnumBridge.To<JournalSourceType>(wantedOrigin);
            query = query.Where(entry => entry.SourceType == stored);
        }

        if (from is { } start)
        {
            query = query.Where(entry => entry.EntryDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(entry => entry.EntryDate <= end);
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(entry => entry.Id.CompareTo(cursorId) < 0);
        }

        // Identifiers are UUIDv7 (time-ordered), so newest-first id order is a stable, unique page key.
        var rows = await query
            .OrderByDescending(entry => entry.Id)
            .Take(size + 1)
            .Select(entry => new JournalRow(
                entry.Id,
                entry.EntryNumber,
                entry.EntryDate,
                entry.Status,
                entry.SourceType,
                entry.TotalDebit,
                entry.TotalCredit,
                entry.Memo,
                entry.PostedAtUtc,
                entry.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        var page = rows
            .Select(row => new JournalEntrySummaryDto(
                row.Id,
                row.EntryNumber,
                row.EntryDate,
                EnumBridge.To<JournalEntryState>(row.Status),
                EnumBridge.To<JournalOrigin>(row.SourceType),
                row.TotalDebit,
                row.TotalCredit,
                row.Memo,
                row.PostedAtUtc,
                row.CreatedAtUtc))
            .ToList();

        return Results.Ok(new PagedResult<JournalEntrySummaryDto>(page, nextCursor, size));
    }

    private static async Task<IResult> GetJournalAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var entry = await db.JournalEntries.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return NotFoundJournal();
        }

        return Results.Ok(await ToDetailDtoAsync(db, entry, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PostJournalAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        PostJournalEntryRequest request,
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

        if (request.Origin == JournalOrigin.Reversal)
        {
            return ApiResults.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "Reversal entries are created by POST /api/v1/journal-entries/{id}/reverse.");
        }

        if (request.Origin == JournalOrigin.Manual && request.SourceId is not null)
        {
            return ApiResults.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "A manual entry must not carry a source id.");
        }

        var (currency, currencyError) = await ResolveCurrencyAsync(db, businessId, request.Currency, cancellationToken).ConfigureAwait(false);

        if (currencyError is not null)
        {
            return currencyError;
        }

        var lines = request.Lines ?? [];

        if (request.Memo is { Length: > MaxMemoLength })
        {
            return ApiResults.ValidationProblem(
                "The journal entry is invalid.",
                new Dictionary<string, string[]> { ["memo"] = [$"A memo of at most {MaxMemoLength} characters is supported."] });
        }

        if (ValidateLines(lines) is { Count: > 0 } lineErrors)
        {
            return ApiResults.ValidationProblem("The journal entry is invalid.", lineErrors);
        }

        var totals = Totals(lines);

        if (totals.Debit != totals.Credit)
        {
            return Unbalanced(totals.Debit, totals.Credit);
        }

        if (await ValidateAccountsAsync(db, businessId, lines, cancellationToken).ConfigureAwait(false) is { } accountError)
        {
            return accountError;
        }

        try
        {
            var entry = await PostEntryAsync(
                db,
                businessId,
                userId,
                currency,
                request.EntryDate,
                request.Memo,
                (JournalSourceType)request.Origin,
                request.SourceId,
                reversalOfEntryId: null,
                lines,
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/journal-entries/{entry.Id}", await ToDetailDtoAsync(db, entry, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> ReverseJournalAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        string? memo,
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

        var original = await db.JournalEntries.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (original is null)
        {
            return NotFoundJournal();
        }

        if (original.Status != EntryStatus.Posted)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "Only posted journal entries can be reversed.");
        }

        if (await db.JournalEntries.AnyAsync(
                entry => entry.BusinessId == businessId
                    && entry.SourceType == JournalSourceType.Reversal
                    && entry.SourceId == original.Id,
                cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyKeyReused,
                "This journal entry already has a reversal; a posted entry can be reversed at most once.");
        }

        var originalLines = await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntryId == original.Id)
            .OrderBy(line => line.LineNo)
            .Select(line => new JournalLineRequest(line.AccountId, line.CreditAmount, line.DebitAmount, line.Description, line.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var reversal = await PostEntryAsync(
                db,
                businessId,
                userId,
                original.Currency,
                original.EntryDate,
                string.IsNullOrWhiteSpace(memo) ? $"Reversal of {original.EntryNumber}" : memo.Trim(),
                JournalSourceType.Reversal,
                original.Id,
                reversalOfEntryId: original.Id,
                originalLines,
                cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/journal-entries/{reversal.Id}",
                await ToDetailDtoAsync(db, reversal, cancellationToken).ConfigureAwait(false));
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

    // ---------------------------------------------------------------- trial balance

    private static async Task<IResult> TrialBalanceAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        DateOnly? from,
        DateOnly? to,
        Guid? accountId,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var rangeTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeFrom = from ?? rangeTo.AddYears(-1);

        if (rangeFrom > rangeTo)
        {
            return ApiResults.ValidationProblem(
                "The date range is invalid.",
                new Dictionary<string, string[]> { ["from"] = ["The start date must not be after the end date."] });
        }

        var activity = await (
                from line in db.JournalLines.AsNoTracking()
                join entry in db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                where line.BusinessId == businessId
                    && entry.BusinessId == businessId
                    && entry.Status == EntryStatus.Posted
                    && entry.EntryDate >= rangeFrom
                    && entry.EntryDate <= rangeTo
                    && (accountId == null || line.AccountId == accountId)
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

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId)
            .ToDictionaryAsync(account => account.Id, cancellationToken)
            .ConfigureAwait(false);

        var rows = activity
            .Where(item => accounts.ContainsKey(item.AccountId))
            .Select(item =>
            {
                var account = accounts[item.AccountId];

                return new TrialBalanceRowDto(
                    account.Id,
                    account.Code,
                    account.Name,
                    (AccountKind)account.AccountType,
                    item.Debit,
                    item.Credit,
                    item.Debit - item.Credit);
            })
            .OrderBy(row => row.Code, StringComparer.Ordinal)
            .ToList();

        return Results.Ok(new TrialBalanceDto(
            rangeFrom,
            rangeTo,
            rows.Sum(row => row.Debit),
            rows.Sum(row => row.Credit),
            rows));
    }

    // ---------------------------------------------------------------- posting core

    /// <summary>
    /// Posts one balanced entry inside a single transaction: the deferred balance trigger fires at COMMIT,
    /// so an entry that reaches the database unbalanced is impossible even if service validation regresses.
    /// </summary>
    private static async Task<JournalEntry> PostEntryAsync(
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
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry starts from a clean slate: state tracked by an aborted attempt must not leak into it.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var entry = await LedgerWriter.PostWithinTransactionAsync(
                db,
                businessId,
                userId,
                currency,
                entryDate,
                memo,
                sourceType,
                sourceId,
                reversalOfEntryId,
                lines,
                cancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return entry;
        }).ConfigureAwait(false);
    }

    /// <summary>
    // ---------------------------------------------------------------- validation helpers

    private static async Task<(string Currency, IResult? Error)> ResolveCurrencyAsync(
        BillingDbContext db,
        Guid businessId,
        string? requested,
        CancellationToken cancellationToken)
    {
        var baseCurrency = await db.Businesses.AsNoTracking()
            .Where(business => business.Id == businessId)
            .Select(business => business.BaseCurrency)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(baseCurrency))
        {
            return ("QAR", null);
        }

        if (!string.IsNullOrWhiteSpace(requested)
            && !string.Equals(requested.Trim(), baseCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return (baseCurrency, ApiResults.ValidationProblem(
                "The journal currency is invalid.",
                new Dictionary<string, string[]> { ["currency"] = [$"This business posts in {baseCurrency}; multi-currency journals are out of scope (spec §3.4)."] }));
        }

        return (baseCurrency, null);
    }

    private static Dictionary<string, string[]> ValidateLines(IReadOnlyList<JournalLineRequest> lines)
    {
        var errors = new Dictionary<string, string[]>();

        if (lines.Count < 2)
        {
            errors["lines"] = ["A posted entry requires at least two lines."];
            return errors;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.Debit < 0 || line.Credit < 0)
            {
                errors[$"lines[{index}]"] = ["Debit and credit must not be negative; use the opposite side instead."];
            }
            else if (line.Debit > 0 && line.Credit > 0)
            {
                errors[$"lines[{index}]"] = ["Exactly one of debit/credit must be greater than zero."];
            }
            else if (line.Debit == 0 && line.Credit == 0)
            {
                errors[$"lines[{index}]"] = ["A line must carry either a debit or a credit."];
            }
            else if (Round(line.Debit) != line.Debit || Round(line.Credit) != line.Credit)
            {
                errors[$"lines[{index}]"] = [$"Amounts support at most {MoneyScale} decimal places."];
            }
        }

        return errors;
    }

    private static async Task<IResult?> ValidateAccountsAsync(
        BillingDbContext db,
        Guid businessId,
        IReadOnlyList<JournalLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var ids = lines.Select(line => line.AccountId).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == businessId && ids.Contains(account.Id))
            .Select(account => new { account.Id, account.IsActive })
            .ToDictionaryAsync(account => account.Id, account => account.IsActive, cancellationToken)
            .ConfigureAwait(false);

        var errors = new Dictionary<string, string[]>();

        for (var index = 0; index < lines.Count; index++)
        {
            if (!accounts.TryGetValue(lines[index].AccountId, out var isActive))
            {
                errors[$"lines[{index}].accountId"] = ["The account does not exist in this business."];
            }
            else if (!isActive)
            {
                errors[$"lines[{index}].accountId"] = ["The account is inactive and cannot be posted against."];
            }
        }

        return errors.Count == 0 ? null : ApiResults.ValidationProblem("The journal entry references invalid accounts.", errors);
    }

    private static (decimal Debit, decimal Credit) Totals(IReadOnlyList<JournalLineRequest> lines)
        => (lines.Sum(line => Round(line.Debit)), lines.Sum(line => Round(line.Credit)));

    private static decimal Round(decimal amount) => decimal.Round(amount, MoneyScale, MidpointRounding.AwayFromZero);

    private static IResult Unbalanced(decimal debit, decimal credit)
        => ApiResults.Problem(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.UnbalancedJournal,
            "The journal entry is not balanced.",
            $"Total debits ({debit}) must equal total credits ({credit}).");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation when exception.ConstraintName == "ux_journal_entries_business_source" =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "The source reference was already posted."),
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The write conflicts with an existing record."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.UnbalancedJournal,
                    "The journal entry violates ledger integrity guards."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The ledger write failed."),
        };

    private static async Task<JournalEntryDto> ToDetailDtoAsync(
        BillingDbContext db,
        JournalEntry entry,
        CancellationToken cancellationToken)
    {
        var lines = await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntryId == entry.Id)
            .OrderBy(line => line.LineNo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var accountIds = lines.Select(line => line.AccountId).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => account.BusinessId == entry.BusinessId && accountIds.Contains(account.Id))
            .Select(account => new { account.Id, account.Code, account.Name })
            .ToDictionaryAsync(account => account.Id, cancellationToken)
            .ConfigureAwait(false);

        return new JournalEntryDto(
            entry.Id,
            entry.EntryNumber,
            entry.EntryDate,
            EnumBridge.To<JournalEntryState>(entry.Status),
            EnumBridge.To<JournalOrigin>(entry.SourceType),
            entry.SourceId,
            entry.Currency,
            entry.TotalDebit,
            entry.TotalCredit,
            entry.Memo,
            entry.PostedAtUtc,
            entry.PostedByUserId,
            entry.ReversalOfEntryId,
            entry.Version,
            entry.CreatedAtUtc,
            lines.Select(line => new JournalLineDto(
                line.Id,
                line.LineNo,
                line.AccountId,
                accounts.TryGetValue(line.AccountId, out var account) ? account.Code : string.Empty,
                accounts.TryGetValue(line.AccountId, out var named) ? named.Name : string.Empty,
                line.DebitAmount,
                line.CreditAmount,
                line.Currency,
                line.Description,
                line.LocationId)).ToList());
    }

    private static AccountDto ToDto(Account account)
        => new(
            account.Id,
            account.Code,
            account.Name,
            EnumBridge.To<AccountKind>(account.AccountType),
            account.ParentId,
            account.IsActive,
            account.Version,
            account.CreatedAtUtc,
            account.UpdatedAtUtc);

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out userId);

    private static IResult NotFound()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The account does not exist in this business.");

    private static IResult NotFoundJournal()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The journal entry does not exist in this business.");

    /// <summary>
    /// Projection shapes carry stored enum values; wire DTOs are built in memory so a text-to-integer
    /// cast can never leak into SQL (see <see cref="EnumBridge"/>).
    /// </summary>
    private sealed record AccountRow(
        Guid Id,
        string Code,
        string Name,
        AccountType AccountType,
        Guid? ParentId,
        bool IsActive,
        long Version,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private sealed record PeriodRow(
        Guid Id,
        string Name,
        DateOnly StartDate,
        DateOnly EndDate,
        AccountingPeriodStatus Status,
        long Version);

    private sealed record JournalRow(
        Guid Id,
        string EntryNumber,
        DateOnly EntryDate,
        EntryStatus Status,
        JournalSourceType SourceType,
        decimal TotalDebit,
        decimal TotalCredit,
        string? Memo,
        DateTimeOffset? PostedAtUtc,
        DateTimeOffset CreatedAtUtc);
}
