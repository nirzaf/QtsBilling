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
        periods.MapGet("/{id:guid}/close-evidence", GetPeriodCloseEvidenceAsync).RequireAuthorization(Permissions.ReportsView);
        periods.MapPost("/{id:guid}/close", ClosePeriodAsync).RequireAuthorization(Permissions.AccountingClose);

        app.MapGet("/api/v1/accounting/periods/{fiscalYear:int}/close-year-evidence", GetYearCloseEvidenceAsync)
            .WithTags("Accounting")
            .RequireAuthorization(Permissions.ReportsView);

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
            (nameof(request.DeliveredNotInvoicedAccountId), request.DeliveredNotInvoicedAccountId, AccountType.Asset),
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
        settings.DeliveredNotInvoicedAccountId = request.DeliveredNotInvoicedAccountId;
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
        settings.DeliveredNotInvoicedAccountId,
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

    /// <summary>
    /// Closes a period. The exclusive row lock that posting takes in share mode still decides whether an entry
    /// belongs to an open window or the close wins - a posting is never silently backdated into a closed period.
    /// A screen-driven close names the version it read and the reason an auditor would see, and rides one stable key,
    /// so a retry answers the same close instead of racing a second decision against the first.
    /// </summary>
    private static async Task<IResult> ClosePeriodAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken,
        CloseAccountingPeriodRequest? request = null)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !TryGetUserId(principal, out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        // A caller that sends a body must explain the close; a reviewer's reason is part of the record, not optional.
        if (request is not null && string.IsNullOrWhiteSpace(request.Reason))
        {
            return ApiResults.ValidationProblem("Closing a period needs a reason.", new Dictionary<string, string[]>
            {
                ["reason"] = ["Give the reason an auditor will read, in 1 to 500 characters."],
            });
        }

        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (request is not null && key.Length is 0 or > 128)
        {
            return ApiResults.ValidationProblem("An idempotency key is required.", new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Send a client-generated key of 1-128 characters; a close is replayed under the same key, not repeated."],
            });
        }

        var reason = request?.Reason?.Trim();
        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "accounting.close-period",
                string.IsNullOrWhiteSpace(key) ? null : key,
                new { id, request },
                async () => await RunClosePeriodAsync(
                    db, businessId, userId, id, request, reason, http, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (execution.Replayed)
            {
                return Results.Ok(execution.Body);
            }

            return execution.StatusCode switch
            {
                StatusCodes.Status200OK => Results.Ok(execution.Body),
                StatusCodes.Status404NotFound => ApiResults.Problem(
                    StatusCodes.Status404NotFound,
                    ApiErrorCodes.NotFound,
                    "The accounting period does not exist in this business."),
                _ => ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.AccountingPeriodClosed,
                    "The accounting period is already closed."),
            };
        }
        catch (DomainRuleException exception)
        {
            return exception.ToResult();
        }
    }

    /// <summary>
    /// Applies one close. The command key already owns the transaction that writes the close and its receipt
    /// together, so the close joins that transaction instead of beginning a second one on the same connection; a
    /// caller that sent no key still gets the bare gate it always had, with the close owning its own transaction.
    /// </summary>
    private static async Task<(int StatusCode, AccountingPeriodDto Body)> RunClosePeriodAsync(
        BillingDbContext db,
        Guid businessId,
        Guid userId,
        Guid id,
        CloseAccountingPeriodRequest? request,
        string? reason,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await ClosePeriodOnceAsync(db, businessId, userId, id, request, reason, http, cancellationToken).ConfigureAwait(false);
        }

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await ClosePeriodOnceAsync(db, businessId, userId, id, request, reason, http, cancellationToken).ConfigureAwait(false);

            if (outcome.StatusCode == StatusCodes.Status200OK)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// One close inside the transaction it is given. The exclusive row lock is the lock every posting takes in
    /// share mode, so a posting either completes before the decision or reads the closed status - never a stale open
    /// one (T10.04). A refused close leaves the period exactly as its reviewer found it.
    /// </summary>
    private static async Task<(int StatusCode, AccountingPeriodDto Body)> ClosePeriodOnceAsync(
        BillingDbContext db,
        Guid businessId,
        Guid userId,
        Guid id,
        CloseAccountingPeriodRequest? request,
        string? reason,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var period = await db.AccountingPeriods
            .FromSql($"SELECT * FROM accounting_periods WHERE business_id = {businessId} AND id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (period is null)
        {
            return (StatusCodes.Status404NotFound, default(AccountingPeriodDto)!);
        }

        if (period.Status != AccountingPeriodStatus.Open)
        {
            return (StatusCodes.Status409Conflict, default(AccountingPeriodDto)!);
        }

        // The version the reviewer read is the version this close is applied to; a period that has already moved is
        // refused rather than closed over somebody else's decision.
        if (request is not null && request.ExpectedVersion != period.Version)
        {
            throw new DomainRuleException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.OptimisticConcurrencyFailure,
                "This period has changed since it was read.",
                $"'{period.Name}' is at version {period.Version}; read the period back before closing it again.");
        }

        period.Status = AccountingPeriodStatus.Closed;

        if (reason is { Length: > 0 })
        {
            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = businessId,
                UserId = userId,
                EntityType = "accounting_period",
                EntityId = period.Id,
                Action = "closed",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CorrelationId = http.TraceIdentifier,
                Summary = System.Text.Json.JsonSerializer.Serialize(new
                {
                    period.Name,
                    period.StartDate,
                    period.EndDate,
                    reason,
                    expectedVersion = request!.ExpectedVersion,
                }),
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (StatusCodes.Status200OK, new AccountingPeriodDto(
            period.Id,
            period.Name,
            period.StartDate,
            period.EndDate,
            AccountingPeriodState.Closed,
            period.Version));
    }

    /// <summary>
    /// What closing this period would leave behind, read from posted facts only: the window's own entries, the
    /// control-account balances the business carries across the boundary, the cash and in-transit positions that are
    /// still moving, and the unfinished acts - a draft journal or an unconfirmed tender - that a close must not walk
    /// past. A balance is shown as a balance; only an unfinished act is called a blocker (task 32, recipe step 3).
    /// </summary>
    private static async Task<IResult> GetPeriodCloseEvidenceAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var period = await db.AccountingPeriods.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (period is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "The accounting period does not exist in this business.");
        }

        var business = await db.Businesses.AsNoTracking()
            .SingleAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
        var settings = await LedgerWriter.ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        var scale = settings?.SettlementScale ?? 2;
        var today = LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);
        var accounts = await LedgerWriter.ResolveAccountsAsync(
            db, businessId, PeriodControlCodes, cancellationToken).ConfigureAwait(false);

        // One read of posted activity, grouped by account: everything below is a slice of it, so two figures on the
        // screen can never be computed against different sets of facts.
        var activity = await (from line in db.JournalLines.AsNoTracking()
                              join entry in db.JournalEntries.AsNoTracking()
                                  on new { line.JournalEntryId, line.BusinessId } equals new { JournalEntryId = entry.Id, entry.BusinessId }
                              where entry.BusinessId == businessId && entry.Status == EntryStatus.Posted
                                  && entry.EntryDate <= period.EndDate
                              select new { line.AccountId, entry.EntryDate, line.DebitAmount, line.CreditAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var netThrough = new Dictionary<Guid, decimal>();
        var windowNet = new Dictionary<Guid, decimal>();
        decimal postedDebit = 0m;
        decimal postedCredit = 0m;
        var postedEntryIds = new HashSet<Guid>();
        foreach (var row in activity)
        {
            var net = row.DebitAmount - row.CreditAmount;
            netThrough[row.AccountId] = netThrough.GetValueOrDefault(row.AccountId) + net;
            var inWindow = row.EntryDate >= period.StartDate;
            if (!inWindow)
            {
                continue;
            }

            windowNet[row.AccountId] = windowNet.GetValueOrDefault(row.AccountId) + net;
            postedDebit += row.DebitAmount;
            postedCredit += row.CreditAmount;
        }

        var postedEntries = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.Status == EntryStatus.Posted
                && entry.EntryDate >= period.StartDate && entry.EntryDate <= period.EndDate)
            .Select(entry => entry.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        postedEntryIds = postedEntries.ToHashSet();
        var draftEntries = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.Status == EntryStatus.Draft
                && entry.EntryDate >= period.StartDate && entry.EntryDate <= period.EndDate)
            .Select(entry => new { entry.Id, entry.TotalDebit })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var windowEnd = PeriodEndUtc(period.EndDate, business.TimeZone);

        var unconfirmed = await db.Payments.AsNoTracking()
            .Where(payment => payment.BusinessId == businessId && payment.Status == PaymentStatus.Pending
                && payment.ReceivedAtUtc < windowEnd)
            .Select(payment => payment.Amount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var openSessions = await db.CashRegisterSessions.AsNoTracking()
            .Where(session => session.BusinessId == businessId && session.Status == CashRegisterSessionStatus.Open
                && session.OpenedAtUtc < windowEnd)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // In transit means dispatched and not yet received: value that left one branch and has not reached the other.
        var transitLines = await (from transfer in db.StockTransfers.AsNoTracking()
                                  join line in db.StockTransferLines.AsNoTracking()
                                      on new { StockTransferId = transfer.Id, transfer.BusinessId } equals new { line.StockTransferId, line.BusinessId }
                                  where transfer.BusinessId == businessId && transfer.Status == StockTransferStatus.Dispatched
                                      && transfer.DispatchedAtUtc < windowEnd
                                  select line.ValueAtDispatch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var transit = LedgerWriter.Round(transitLines.Sum(value => value ?? 0m));

        var stockValue = LedgerWriter.Round(await db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.BusinessId == businessId)
            .SumAsync(balance => balance.ValueOnHand, cancellationToken)
            .ConfigureAwait(false));

        decimal Control(string code, bool debitSide)
            => accounts.TryGetValue(code, out var account)
                ? LedgerWriter.Round(debitSide ? netThrough.GetValueOrDefault(account.Id) : -netThrough.GetValueOrDefault(account.Id))
                : 0m;

        var revenue = LedgerWriter.Round(PeriodControlCodes
            .Where(code => code is "4000" or "4010")
            .Select(code => accounts.TryGetValue(code, out var account) ? -windowNet.GetValueOrDefault(account.Id) : 0m)
            .Sum());
        var expenses = LedgerWriter.Round(PeriodControlCodes
            .Where(code => code is "5000" or "5150" or "5200")
            .Select(code => accounts.TryGetValue(code, out var account) ? windowNet.GetValueOrDefault(account.Id) : 0m)
            .Sum());
        var netIncome = LedgerWriter.Round(revenue - expenses);

        var outstanding = new List<PeriodOutstandingDto>
        {
            new("draft-journals", "Draft journals in the window", draftEntries.Count, LedgerWriter.Round(draftEntries.Sum(entry => entry.TotalDebit)), true,
                "A draft is an unfinished act inside the period. Post it or delete it before the window closes; a close does not carry a draft forward."),
            new("unconfirmed-payments", "Tenders not yet confirmed", unconfirmed.Count, LedgerWriter.Round(unconfirmed.Sum()), true,
                "Money the server recorded but nobody confirmed. Confirm or bounce each tender, because a close cannot say whether it was received."),
            new("open-sessions", "Register sessions still open", openSessions.Count, 0m, true,
                "A terminal holding uncounted cash keeps custody open (D07). Close the session, or carry its obligation forward under a named reason."),
            new("in-transit", "Stock in transit at the period end", transitLines.Count, transit, false,
                "Value that has left one branch and not reached another. It stays an asset in transit and is reconciled when the delivery is received."),
            new("receivable", "Receivables owed to the business", 0, Control("1100", true), false,
                "An open receivable is a balance the close carries forward, not an error - the statement for each customer is what proves it."),
            new("payable", "Payables the business owes", 0, Control("2000", false), false,
                "An open payable is carried across the boundary; the supplier statements and the GRNI clearing account are what prove it."),
            new("deposits", "Customer deposits held", 0, Control("2105", false), false,
                "Money received and not yet applied stays a liability until a settlement or a refund answers it."),
            new("cash", "Cash on hand per the ledger", 0, Control("1000", true), false,
                "The till balance the ledger carries; it is proved by the drawer counts, not by a figure typed here."),
            new("stock", "Stock value per the ledger", 0, Control("1200", true), false,
                $"The inventory account carries {stockValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} across all branches; the two are read together, never reconciled by hand."),
        };

        var year = period.EndDate.Year;
        var yearPeriods = await db.AccountingPeriods.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StartDate <= new DateOnly(year, 12, 31) && row.EndDate >= new DateOnly(year, 1, 1))
            .OrderBy(row => row.StartDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var yearComplete = yearPeriods.Count > 0
            && yearPeriods.All(row => row.Status != AccountingPeriodStatus.Open)
            && yearPeriods.OrderByDescending(row => row.EndDate).First().EndDate >= new DateOnly(year, 12, 31);
        var gapFrom = YearGapStart(yearPeriods, year);

        return Results.Ok(new PeriodCloseEvidenceDto(
            new AccountingPeriodDto(period.Id, period.Name, period.StartDate, period.EndDate,
                EnumBridge.To<AccountingPeriodState>(period.Status), period.Version),
            today, business.TimeZone, scale,
            period.Status == AccountingPeriodStatus.Open,
            LedgerWriter.Round(postedDebit), LedgerWriter.Round(postedCredit), postedEntryIds.Count,
            netIncome,
            Control("1100", true), Control("2000", false), Control("1200", true), transit, Control("2105", false),
            Control("1000", true), openSessions.Count, draftEntries.Count, unconfirmed.Count,
            outstanding, yearComplete, yearPeriods.Any(row => row.Status == AccountingPeriodStatus.Locked),
            gapFrom is { } gap ? gap.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
            await TrialBalanceRowsAsync(db, businessId, period.StartDate, period.EndDate, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>The year-end position, computed from the same facts the close itself reads.</summary>
    private static async Task<IResult> GetYearCloseEvidenceAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        int fiscalYear,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        if (fiscalYear is < 1900 or > 9998)
        {
            return ApiResults.ValidationProblem("The fiscal year is invalid.", new Dictionary<string, string[]>
            {
                ["fiscalYear"] = ["Enter a year from 1900 through 9998."],
            });
        }

        var start = new DateOnly(fiscalYear, 1, 1);
        var end = new DateOnly(fiscalYear, 12, 31);
        var business = await db.Businesses.AsNoTracking()
            .SingleAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
        var settings = await LedgerWriter.ResolveSettingsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        var accounts = await LedgerWriter.ResolveAccountsAsync(
            db, businessId, PeriodControlCodes, cancellationToken).ConfigureAwait(false);
        var periods = await db.AccountingPeriods.AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.StartDate <= end && row.EndDate >= start)
            .OrderBy(row => row.StartDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var gapFrom = YearGapStart(periods, fiscalYear);

        var window = await (from line in db.JournalLines.AsNoTracking()
                            join entry in db.JournalEntries.AsNoTracking()
                                on new { line.JournalEntryId, line.BusinessId } equals new { JournalEntryId = entry.Id, entry.BusinessId }
                            where entry.BusinessId == businessId && entry.Status == EntryStatus.Posted
                                && entry.EntryDate >= start && entry.EntryDate <= end
                            select new { line.AccountId, line.DebitAmount, line.CreditAmount })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        decimal Net(string code) => accounts.TryGetValue(code, out var account)
            ? window.Where(row => row.AccountId == account.Id).Sum(row => row.DebitAmount - row.CreditAmount)
            : 0m;

        var revenue = LedgerWriter.Round(-Net("4000") - Net("4010"));
        var expenses = LedgerWriter.Round(Net("5000") + Net("5150") + Net("5200"));
        var retained = accounts.TryGetValue("3000", out var retainedAccount) ? retainedAccount : null;
        var closingEntries = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.SourceType == JournalSourceType.FiscalYearClose
                && entry.EntryDate >= start && entry.EntryDate <= end)
            .OrderBy(entry => entry.EntryNumber)
            .Select(entry => new JournalEntrySummaryDto(
                entry.Id, entry.EntryNumber, entry.EntryDate, EnumBridge.To<JournalEntryState>(entry.Status),
                EnumBridge.To<JournalOrigin>(entry.SourceType), entry.TotalDebit, entry.TotalCredit, entry.Memo,
                entry.PostedAtUtc, entry.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var alreadyClosed = closingEntries.Count > 0 || periods.Any(row => row.Status == AccountingPeriodStatus.Locked);
        var draftEntries = await db.JournalEntries.AsNoTracking()
            .Where(entry => entry.BusinessId == businessId && entry.Status == EntryStatus.Draft
                && entry.EntryDate >= start && entry.EntryDate <= end)
            .Select(entry => entry.TotalDebit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var outstanding = new List<PeriodOutstandingDto>
        {
            new("open-periods", "Accounting periods still open", periods.Count(row => row.Status == AccountingPeriodStatus.Open), 0m, true,
                "Each open period is closed on its own screen, with its evidence, before the year transfers anything."),
            new("coverage", "Days the periods do not cover", gapFrom is null ? 0 : 1, 0m, true,
                gapFrom is null ? "The year's periods run end to end, so the transfer has a complete window."
                    : $"No period covers {gapFrom:yyyy-MM-dd}; the year cannot be closed over a gap."),
            new("draft-journals", "Draft journals in the year", draftEntries.Count, LedgerWriter.Round(draftEntries.Sum()), true,
                "A draft is unfinished, not merely quiet. It is posted or deleted before the year closes."),
            new("retained-earnings", "Retained earnings account", retained is null ? 0 : 1, 0m, true,
                "The transfer closes revenue and expense into an equity account. Without a configured active equity account there is nowhere to put the year's result."),
        };

        return Results.Ok(new FiscalYearEvidenceDto(
            fiscalYear, start, end,
            LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow), business.TimeZone, settings?.SettlementScale ?? 2,
            periods.Count, periods.Count(row => row.Status == AccountingPeriodStatus.Open),
            periods.Count(row => row.Status == AccountingPeriodStatus.Locked),
            periods.Count > 0 && gapFrom is null && periods.OrderByDescending(row => row.EndDate).First().EndDate >= end,
            alreadyClosed,
            retained is not null && retained.IsActive && retained.AccountType == AccountType.Equity,
            retained?.Code,
            gapFrom is { } gap ? gap.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
            revenue, expenses, LedgerWriter.Round(revenue - expenses),
            outstanding, closingEntries));
    }

    private static readonly string[] PeriodControlCodes =
        ["1000", "1100", "1200", "2000", "2105", "3000", "4000", "4010", "5000", "5150", "5200"];

    /// <summary>
    /// The instant a business date stops being "today" for this business, in UTC. Every "as at the period end" read
    /// uses it, so a tender received after the local midnight is not counted as if it had arrived inside the window
    /// (specification F21 - one clock, the business's own).
    /// </summary>
    private static DateTimeOffset PeriodEndUtc(DateOnly date, string? timeZoneId)
    {
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        var localStartOfNextDay = new DateTime(date.Year, date.Month, date.Day, 23, 59, 59, DateTimeKind.Unspecified);
        return new DateTimeOffset(localStartOfNextDay, zone.GetUtcOffset(localStartOfNextDay)).ToUniversalTime();
    }

    /// <summary>The first day of the year no period covers, or null when the windows run end to end.</summary>
    private static DateOnly? YearGapStart(IReadOnlyList<AccountingPeriod> periods, int fiscalYear)
    {
        var cursor = new DateOnly(fiscalYear, 1, 1);
        var end = new DateOnly(fiscalYear, 12, 31);
        foreach (var period in periods.OrderBy(row => row.StartDate))
        {
            if (period.StartDate > cursor)
            {
                return cursor;
            }

            if (period.EndDate >= end)
            {
                return null;
            }

            cursor = period.EndDate.AddDays(1);
        }

        return cursor <= end ? cursor : null;
    }

    /// <summary>Trial balance rows for a window, derived from posted lines through the account's own activity.</summary>
    private static async Task<IReadOnlyList<TrialBalanceRowDto>> TrialBalanceRowsAsync(
        BillingDbContext db, Guid businessId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        var rows = await (from line in db.JournalLines.AsNoTracking()
                          join entry in db.JournalEntries.AsNoTracking()
                              on new { line.JournalEntryId, line.BusinessId } equals new { JournalEntryId = entry.Id, entry.BusinessId }
                          join account in db.Accounts.AsNoTracking()
                              on new { line.AccountId, line.BusinessId } equals new { AccountId = account.Id, account.BusinessId }
                          where entry.BusinessId == businessId && entry.Status == EntryStatus.Posted
                              && entry.EntryDate >= startDate && entry.EntryDate <= endDate
                          group new { line, account } by new { account.Id, account.Code, account.Name, account.AccountType } into grouped
                          select new TrialBalanceRowDto(
                              grouped.Key.Id,
                              grouped.Key.Code,
                              grouped.Key.Name,
                              EnumBridge.To<AccountKind>(grouped.Key.AccountType),
                              grouped.Sum(row => row.line.DebitAmount),
                              grouped.Sum(row => row.line.CreditAmount),
                              grouped.Sum(row => row.line.DebitAmount - row.line.CreditAmount)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.OrderBy(row => row.Code, StringComparer.Ordinal).ToList();
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
            // A stable key makes the posting exactly-once across a lost response. Without one the entry keeps the
            // gates it always had - balance, account type and tenant, the period the effective date falls in.
            var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "journal.post",
                key.Length == 0 ? null : key,
                request,
                async () =>
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

                    return (StatusCodes.Status201Created, await ToDetailDtoAsync(db, entry, cancellationToken).ConfigureAwait(false));
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/journal-entries/{execution.Body.Id}", execution.Body);
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

    /// <summary>
    /// Reverses a posted entry. The correction may be dated the day it is recorded rather than the day of the entry
    /// it answers, which is the only way a mistaken posting can be corrected after its period has closed; the chosen
    /// date is still gated by the ledger's own period rules, and the reversal is pointed at the original by the
    /// server, never by a caller-supplied reference. The original entry is never edited or deleted (D10).
    /// </summary>
    private static async Task<IResult> ReverseJournalAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        HttpContext http,
        CancellationToken cancellationToken,
        ReverseJournalRequest? request = null,
        string? memo = null)
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

        var reason = request?.Reason ?? memo;
        var correctionDate = request?.CorrectionDate ?? original.EntryDate;
        if (correctionDate < original.EntryDate)
        {
            return ApiResults.ValidationProblem("The correction date is invalid.", new Dictionary<string, string[]>
            {
                [nameof(correctionDate)] = [$"A correction is dated {original.EntryDate:yyyy-MM-dd} or later; it cannot be placed before the entry it answers."],
            });
        }

        if (request is { CorrectionDate: not null })
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return ApiResults.ValidationProblem("A dated correction needs a reason.", new Dictionary<string, string[]>
                {
                    ["reason"] = ["Give the reason an auditor will read, in 1 to 500 characters."],
                });
            }

            var business = await db.Businesses.AsNoTracking()
                .SingleAsync(row => row.Id == businessId, cancellationToken).ConfigureAwait(false);
            var today = LedgerWriter.LocalDate(business.TimeZone, DateTimeOffset.UtcNow);
            if (correctionDate > today)
            {
                return ApiResults.ValidationProblem("The correction date is invalid.", new Dictionary<string, string[]>
                {
                    [nameof(correctionDate)] = [$"The business date is {today:yyyy-MM-dd}; a correction cannot be recorded in a future period."],
                });
            }
        }

        var reversalMemo = string.IsNullOrWhiteSpace(reason) ? $"Reversal of {original.EntryNumber}" : reason.Trim();
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();

        try
        {
            var execution = await IdempotencyExecutor.ExecuteAsync(
                db,
                businessId,
                "journal.reverse",
                key.Length == 0 ? null : key,
                new { id, correctionDate, reason },
                async () =>
                {
                    var reversal = await PostEntryAsync(
                        db,
                        businessId,
                        userId,
                        original.Currency,
                        correctionDate,
                        reversalMemo,
                        JournalSourceType.Reversal,
                        original.Id,
                        reversalOfEntryId: original.Id,
                        originalLines,
                        cancellationToken).ConfigureAwait(false);

                    return (StatusCodes.Status201Created, await ToDetailDtoAsync(db, reversal, cancellationToken).ConfigureAwait(false));
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/journal-entries/{execution.Body.Id}", execution.Body);
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
        // Inside an idempotency command the caller already holds the transaction and its retry strategy, so the
        // entry is written into that transaction and committed together with the receipt - a second transaction on
        // the same connection would be refused by the provider before anything was posted.
        if (db.Database.CurrentTransaction is not null)
        {
            return await PostEntryOnceAsync(
                db, businessId, userId, currency, entryDate, memo, sourceType, sourceId, reversalOfEntryId, lines,
                cancellationToken).ConfigureAwait(false);
        }

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry starts from a clean slate: state tracked by an aborted attempt must not leak into it.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var entry = await PostEntryOnceAsync(
                db, businessId, userId, currency, entryDate, memo, sourceType, sourceId, reversalOfEntryId, lines,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return entry;
        }).ConfigureAwait(false);
    }

    /// <summary>Writes the entry and its lines in the transaction the caller owns; the caller commits it.</summary>
    private static async Task<JournalEntry> PostEntryOnceAsync(
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

        return entry;
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
