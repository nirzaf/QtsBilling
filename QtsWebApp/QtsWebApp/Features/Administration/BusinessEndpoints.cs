using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Sync;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Administration;

/// <summary>
/// Owner-facing business and branch setup (specification task 06, requirements R03/R04/R05/R07). No normal
/// setup step needs SQL: identity, time zone, fiscal calendar, numbering and branches are all here, while
/// anything that would silently re-price history is refused instead of repaired.
/// </summary>
/// <remarks>
/// Control-account mappings keep their existing authority at <c>/api/v1/accounting/settings</c>; this
/// surface reads settlement scale from it and never writes a competing copy.
/// </remarks>
public static class BusinessEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxLegalNameLength = 300;
    private const int MaxCodeLength = 64;
    private const int MaxAddressLength = 2_000;
    private const int MaxTimeZoneLength = 64;
    private const int MaxReasonLength = 500;

    public static IEndpointRouteBuilder MapBusinessAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/business-settings").WithTags("Administration");
        settings.MapGet("/", GetSettingsAsync).RequireAuthorization(Permissions.BusinessSettingsManage);
        settings.MapPut("/", SaveSettingsAsync).RequireAuthorization(Permissions.BusinessSettingsManage);

        // Branch commands only: the list stays on the existing GET /api/v1/locations route so one authority
        // answers it, and a location is deactivated rather than deleted, because posted rows point at it.
        app.MapPost("/api/v1/locations", async (
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                CreateLocationRequest request,
                CancellationToken cancellationToken) =>
            await CreateLocationAsync(principal, db, http, request, cancellationToken))
            .WithTags("Administration")
            .RequireAuthorization(Permissions.BusinessSettingsManage);

        app.MapPut("/api/v1/locations/{id:guid}", async (
                Guid id,
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                UpdateLocationRequest request,
                CancellationToken cancellationToken) =>
            await UpdateLocationAsync(id, principal, db, http, request, cancellationToken))
            .WithTags("Administration")
            .RequireAuthorization(Permissions.BusinessSettingsManage);

        app.MapPost("/api/v1/locations/{id:guid}/deactivate", async (
                Guid id,
                ClaimsPrincipal principal,
                BillingDbContext db,
                HttpContext http,
                DeactivateLocationRequest request,
                CancellationToken cancellationToken) =>
            await DeactivateLocationAsync(id, principal, db, http, request, cancellationToken))
            .WithTags("Administration")
            .RequireAuthorization(Permissions.BusinessSettingsManage);

        return app;
    }

    private static async Task<IResult> GetSettingsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var business = await db.Businesses.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return NotFound();
        }

        return Results.Ok(await BuildDtoAsync(db, business, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> SaveSettingsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        SaveBusinessSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var business = await db.Businesses
            .SingleOrDefaultAsync(row => row.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return NotFound();
        }

        if (request.ExpectedVersion != business.Version)
        {
            return StaleVersion(business.Version);
        }

        var validation = ValidateIdentity(request);
        if (validation is not null)
        {
            return validation;
        }

        var settings = await db.BusinessAccountingSettings
            .SingleOrDefaultAsync(row => row.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);

        var numberingValidation = ValidateNumbering(request.Numbering, settings);
        if (numberingValidation is not null)
        {
            return numberingValidation;
        }

        // Currency, settlement precision and the fiscal boundary are financial identity. Once a posted fact
        // depends on them they cannot be moved by a general PUT: history is never repriced underneath.
        var posted = await HasPostedFactsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
        if (posted)
        {
            var refused = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.BaseCurrency)
                && !string.Equals(request.BaseCurrency.Trim(), business.BaseCurrency, StringComparison.Ordinal))
            {
                refused.Add("base currency");
            }

            if (request.SettlementScale is { } scale && settings is not null && scale != settings.SettlementScale)
            {
                refused.Add("settlement scale");
            }

            if (request.FiscalYearStartMonth != business.FiscalYearStartMonth)
            {
                refused.Add("fiscal year start month");
            }

            if (refused.Count > 0)
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    $"Posted facts already depend on {string.Join(" and ", refused)}.",
                    "Change them only through a finance-approved effective-date transition, never through this command.");
            }
        }

        var now = DateTimeOffset.UtcNow;

        try
        {
            await SyncTransaction.RunAsync(db, async () =>
            {
                business.Name = request.Name.Trim();
                business.LegalName = string.IsNullOrWhiteSpace(request.LegalName) ? null : request.LegalName.Trim();
                business.TimeZone = request.TimeZone.Trim();
                business.FiscalYearStartMonth = request.FiscalYearStartMonth;

                if (!string.IsNullOrWhiteSpace(request.BaseCurrency))
                {
                    business.BaseCurrency = request.BaseCurrency.Trim().ToUpperInvariant();
                }

                if (settings is not null)
                {
                    if (request.SettlementScale is { } newScale)
                    {
                        settings.SettlementScale = newScale;
                    }

                    if (request.Numbering is { } numbering)
                    {
                        settings.InvoicePrefix = numbering.InvoicePrefix.Trim();
                        settings.InvoicePadding = numbering.InvoicePadding;
                    }
                }

                // An untouched series adopts the newly configured presentation immediately; a series that
                // already handed out a number keeps the number it published, so no document changes identity.
                if (request.Numbering is { } applied && settings is not null)
                {
                    var untouched = await db.InvoiceSequences
                        .Where(row => row.BusinessId == businessId && row.NextNumber == 1)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var sequence in untouched)
                    {
                        sequence.Prefix = applied.InvoicePrefix.Trim();
                        sequence.Padding = applied.InvoicePadding;
                    }
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    BusinessId = businessId,
                    UserId = userId,
                    EntityType = "business_settings",
                    EntityId = businessId,
                    Action = "updated",
                    OccurredAtUtc = now,
                    CorrelationId = http.TraceIdentifier,
                    Summary = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        name = business.Name,
                        timeZone = business.TimeZone,
                        fiscalYearStartMonth = business.FiscalYearStartMonth,
                        currency = business.BaseCurrency,
                        settlementScale = settings?.SettlementScale,
                        numbering = settings is null ? null : new { prefix = settings.InvoicePrefix, padding = settings.InvoicePadding },
                        // The context stamps the next version itself; the trail records what it will be.
                        version = business.Version + 1,
                    }),
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DuplicateName,
                "Another business already trades under that name.",
                "Business names are unique on this instance; use the legal name to distinguish branches of the same trade.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(business.Version);
        }

        return Results.Ok(await BuildDtoAsync(db, business, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateLocationAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        CreateLocationRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors[nameof(request.Name)] = [$"Enter a branch name of 1-{MaxNameLength} characters."];
        }

        var code = request.Code?.Trim();
        if (code is { Length: > MaxCodeLength })
        {
            errors[nameof(request.Code)] = [$"A branch code is at most {MaxCodeLength} characters."];
        }

        if (request.Address is { Length: > MaxAddressLength })
        {
            errors[nameof(request.Address)] = [$"A branch address is at most {MaxAddressLength} characters."];
        }

        if (!IsValidTimeZone(request.TimeZone, out var timeZoneError, out var zone))
        {
            errors[nameof(request.TimeZone)] = [timeZoneError!];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The branch payload is invalid.", errors);
        }

        var location = new BusinessLocation
        {
            BusinessId = businessId,
            Name = name,
            Code = string.IsNullOrWhiteSpace(code) ? null : code,
            Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
            TimeZone = zone,
            IsActive = true,
            Version = 1,
        };

        var now = DateTimeOffset.UtcNow;

        try
        {
            await SyncTransaction.RunAsync(db, async () =>
            {
                db.BusinessLocations.Add(location);
                db.AuditEvents.Add(LocationAudit(businessId, userId, location.Id, "created", now, http.TraceIdentifier, new { name = location.Name, code = location.Code }));
                await SyncProjections.MasterDataAsync(db, businessId, "location", location.Id, LocationProjection(location, location.Version), false, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DuplicateName,
                "This business already has a branch with that code.",
                "Use a different branch code, or leave the code empty.");
        }

        return Results.Created($"/api/v1/locations/{location.Id}", ToDto(location));
    }

    private static async Task<IResult> UpdateLocationAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        UpdateLocationRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        var location = await db.BusinessLocations
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return NotFound();
        }

        if (request.ExpectedVersion != location.Version)
        {
            return StaleVersion(location.Version);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors[nameof(request.Name)] = [$"Enter a branch name of 1-{MaxNameLength} characters."];
        }

        if (request.Code?.Trim() is { Length: > MaxCodeLength } tooLongCode)
        {
            errors[nameof(request.Code)] = [$"A branch code is at most {MaxCodeLength} characters (received {tooLongCode.Length})."];
        }

        if (request.Address is { Length: > MaxAddressLength })
        {
            errors[nameof(request.Address)] = [$"A branch address is at most {MaxAddressLength} characters."];
        }

        if (!IsValidTimeZone(request.TimeZone, out var timeZoneError, out var zone))
        {
            errors[nameof(request.TimeZone)] = [timeZoneError!];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The branch payload is invalid.", errors);
        }

        if (!location.IsActive)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This branch is deactivated.",
                "Reactivation is not a branch edit; it is a separate reviewed decision because offline authority may have moved.");
        }

        var now = DateTimeOffset.UtcNow;

        try
        {
            await SyncTransaction.RunAsync(db, async () =>
            {
                location.Name = name;
                location.Code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim();
                location.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
                location.TimeZone = zone;

                db.AuditEvents.Add(LocationAudit(businessId, userId, location.Id, "updated", now, http.TraceIdentifier, new { name = location.Name, code = location.Code, version = location.Version + 1 }));
                await SyncProjections.MasterDataAsync(db, businessId, "location", location.Id, LocationProjection(location, location.Version + 1), false, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DuplicateName,
                "Another branch already uses that code.",
                "Choose a code that is free inside this business.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(location.Version);
        }

        return Results.Ok(ToDto(location));
    }

    private static async Task<IResult> DeactivateLocationAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpContext http,
        DeactivateLocationRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId) || !principal.TryGetCurrentUserId(out var userId))
        {
            return BusinessScope.MissingMembership();
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > MaxReasonLength)
        {
            return ApiResults.ValidationProblem("The branch needs a reason to leave service.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = [$"Enter a reason of 1-{MaxReasonLength} characters."],
            });
        }

        var location = await db.BusinessLocations
            .SingleOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return NotFound();
        }

        if (request.ExpectedVersion != location.Version)
        {
            return StaleVersion(location.Version);
        }

        if (!location.IsActive)
        {
            return Results.Ok(ToDto(location));
        }

        // Money and stock that are still live at the branch keep it in service: deactivating would strand
        // an open drawer, an in-transit consignment, an uncounted session or an enrolled till's authority.
        var blockers = new List<string>();

        if (await db.CashRegisterSessions.AnyAsync(session => session.BusinessId == businessId
                && session.Status == CashRegisterSessionStatus.Open
                && db.CashRegisters.Any(register => register.BusinessId == businessId
                    && register.Id == session.CashRegisterId
                    && register.LocationId == id), cancellationToken).ConfigureAwait(false))
        {
            blockers.Add("an open cash drawer session");
        }

        if (await db.StockTransfers.AnyAsync(transfer => transfer.BusinessId == businessId
                && (transfer.SourceLocationId == id || transfer.DestinationLocationId == id)
                && transfer.Status == StockTransferStatus.Dispatched, cancellationToken).ConfigureAwait(false))
        {
            blockers.Add("a stock transfer in transit");
        }

        if (await db.StockCountSessions.AnyAsync(count => count.BusinessId == businessId
                && count.LocationId == id
                && (count.Status == StockCountStatus.Open || count.Status == StockCountStatus.Submitted), cancellationToken).ConfigureAwait(false))
        {
            blockers.Add("an unfinished stock count");
        }

        if (await db.SalesOrders.AnyAsync(order => order.BusinessId == businessId
                && order.LocationId == id
                && (order.Status == SalesOrderStatus.Confirmed || order.Status == SalesOrderStatus.PartiallyFulfilled), cancellationToken).ConfigureAwait(false))
        {
            blockers.Add("an order awaiting delivery");
        }

        var enrolledDevices = await db.DeviceRegistrations.CountAsync(device => device.BusinessId == businessId
            && device.LocationId == id
            && device.Status == DeviceStatus.Active, cancellationToken).ConfigureAwait(false);

        if (enrolledDevices > 0)
        {
            blockers.Add($"{enrolledDevices} enrolled device(s) still hold offline authority here");
        }

        if (blockers.Count > 0)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This branch cannot leave service yet.",
                $"Still open here: {string.Join(", ", blockers)}.");
        }

        var now = DateTimeOffset.UtcNow;

        await SyncTransaction.RunAsync(db, async () =>
        {
            location.IsActive = false;

            db.AuditEvents.Add(LocationAudit(businessId, userId, location.Id, "deactivated", now, http.TraceIdentifier, new { reason = request.Reason.Trim(), version = location.Version + 1 }));
            await SyncProjections.MasterDataAsync(db, businessId, "location", location.Id, LocationProjection(location, location.Version + 1), false, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(location));
    }

    // ------------------------------------------------------------------ the setup view

    private static async Task<BusinessSettingsDto> BuildDtoAsync(
        BillingDbContext db,
        Business business,
        CancellationToken cancellationToken)
    {
        var settings = await db.BusinessAccountingSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BusinessId == business.Id, cancellationToken)
            .ConfigureAwait(false);

        var locations = await db.BusinessLocations.AsNoTracking()
            .Where(row => row.BusinessId == business.Id)
            .OrderBy(row => row.Name)
            .Select(row => new BusinessLocationDto(row.Id, row.Name, row.Code, row.Address, row.IsActive, row.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var blocking = await SetupBlockersAsync(db, business, settings, locations, cancellationToken).ConfigureAwait(false);

        return new BusinessSettingsDto(
            business.Id,
            business.Name,
            business.LegalName,
            business.TimeZone,
            business.BaseCurrency,
            business.FiscalYearStartMonth,
            business.Version,
            settings?.SettlementScale,
            settings is null ? null : new NumberingSettingsDto(settings.InvoicePrefix, settings.InvoicePadding),
            locations,
            new BusinessSetupCompletenessDto(blocking.Count == 0, blocking));
    }

    /// <summary>
    /// The exact reason a first posting would fail, so the owner is told before the operator meets a
    /// period or account error. Defaults populate a draft configuration; they never claim legal or tax
    /// certification, which stays a human decision.
    /// </summary>
    private static async Task<List<string>> SetupBlockersAsync(
        BillingDbContext db,
        Business business,
        BusinessAccountingSettings? settings,
        IReadOnlyList<BusinessLocationDto> locations,
        CancellationToken cancellationToken)
    {
        var blocking = new List<string>();

        if (!locations.Any(row => row.IsActive))
        {
            blocking.Add("Create at least one active branch location.");
        }

        if (settings is null)
        {
            blocking.Add("Configure the control accounts and settlement scale under accounting settings.");
        }
        else
        {
            var requiredAccountIds = new[]
            {
                settings.CashAccountId, settings.BankClearingAccountId, settings.AccountsReceivableAccountId,
                settings.InventoryAssetAccountId, settings.DeliveredNotInvoicedAccountId, settings.InputTaxReceivableAccountId,
                settings.AccountsPayableAccountId, settings.OutputTaxPayableAccountId, settings.GrniClearingAccountId,
                settings.CustomerDepositsAccountId, settings.RetainedEarningsAccountId, settings.SalesRevenueAccountId,
                settings.SalesReturnsAccountId, settings.CogsAccountId, settings.PurchasePriceVarianceAccountId,
                settings.InventoryShrinkageAccountId, settings.CashOverShortAccountId,
            };

            var usable = await db.Accounts.CountAsync(account => account.BusinessId == business.Id
                && account.IsActive
                && requiredAccountIds.Contains(account.Id), cancellationToken).ConfigureAwait(false);

            if (usable < requiredAccountIds.Distinct().Count())
            {
                blocking.Add("One or more control accounts are missing, inactive, or belong to another business.");
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var openPeriod = await db.AccountingPeriods.AnyAsync(period => period.BusinessId == business.Id
            && period.Status == AccountingPeriodStatus.Open
            && period.StartDate <= today
            && period.EndDate >= today, cancellationToken).ConfigureAwait(false);

        if (!openPeriod)
        {
            blocking.Add("Open an accounting period covering today; posting refuses without one.");
        }

        return blocking;
    }

    // ------------------------------------------------------------------ validation helpers

    private static IResult? ValidateIdentity(SaveBusinessSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors[nameof(request.Name)] = [$"Enter a trading name of 1-{MaxNameLength} characters."];
        }

        if (request.LegalName is { Length: > MaxLegalNameLength })
        {
            errors[nameof(request.LegalName)] = [$"A legal name is at most {MaxLegalNameLength} characters."];
        }

        if (!IsValidTimeZone(request.TimeZone, out var timeZoneError, out _))
        {
            errors[nameof(request.TimeZone)] = [timeZoneError!];
        }

        if (request.FiscalYearStartMonth is < 1 or > 12)
        {
            errors[nameof(request.FiscalYearStartMonth)] = ["The fiscal year must start in a month between 1 and 12."];
        }

        if (!string.IsNullOrWhiteSpace(request.BaseCurrency)
            && (request.BaseCurrency!.Trim().Length != 3 || !request.BaseCurrency.Trim().All(char.IsLetter)))
        {
            errors[nameof(request.BaseCurrency)] = ["A base currency is a three-letter ISO code."];
        }

        if (request.SettlementScale is { } scale && scale is not (0 or 2 or 3 or 4))
        {
            errors[nameof(request.SettlementScale)] = ["Settlement scale must be 0, 2, 3, or 4."];
        }

        return errors.Count == 0 ? null : ApiResults.ValidationProblem("The business settings payload is invalid.", errors);
    }

    private static IResult? ValidateNumbering(NumberingSettingsDto? numbering, BusinessAccountingSettings? settings)
    {
        if (numbering is null)
        {
            return null;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var prefix = numbering.InvoicePrefix?.Trim() ?? string.Empty;

        if (prefix.Length is 0 or > 12 || !System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[A-Za-z0-9][A-Za-z0-9_-]*$"))
        {
            errors[nameof(numbering.InvoicePrefix)] = ["A document prefix is 1-12 letters, digits, hyphen or underscore, starting with a letter or digit."];
        }

        if (numbering.InvoicePadding is < 3 or > 12)
        {
            errors[nameof(numbering.InvoicePadding)] = ["Number padding must be between 3 and 12 digits."];
        }

        if (settings is null)
        {
            errors[nameof(numbering)] = ["Configure the accounting settings first; numbering is stored with them."];
        }

        return errors.Count == 0 ? null : ApiResults.ValidationProblem("The document numbering payload is invalid.", errors);
    }

    private static bool IsValidTimeZone(string? value, out string? error, out string? zone)
    {
        zone = null;
        error = null;

        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Choose the time zone the business trades in.";
            return false;
        }

        if (candidate.Length > MaxTimeZoneLength)
        {
            error = $"A time zone identifier is at most {MaxTimeZoneLength} characters.";
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(candidate).Id;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            error = "That time zone is not available on this server.";
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            error = "That time zone is registered but unusable on this server.";
            return false;
        }
    }

    private static async Task<bool> HasPostedFactsAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken)
        => await db.JournalEntries.AnyAsync(entry => entry.BusinessId == businessId, cancellationToken).ConfigureAwait(false)
        || await db.Invoices.AnyAsync(invoice => invoice.BusinessId == businessId && invoice.Status != InvoiceStatus.Draft, cancellationToken).ConfigureAwait(false)
        || await db.Sales.AnyAsync(sale => sale.BusinessId == businessId, cancellationToken).ConfigureAwait(false);

    private static AuditEvent LocationAudit(Guid businessId, Guid userId, Guid locationId, string action, DateTimeOffset now, string correlationId, object summary)
        => new()
        {
            BusinessId = businessId,
            UserId = userId,
            EntityType = "business_location",
            EntityId = locationId,
            Action = action,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
            Summary = System.Text.Json.JsonSerializer.Serialize(summary),
        };

    private static object LocationProjection(BusinessLocation location, long version) => new
    {
        id = location.Id,
        name = location.Name,
        code = location.Code,
        isActive = location.IsActive,
        version,
    };

    private static BusinessLocationDto ToDto(BusinessLocation location)
        => new(location.Id, location.Name, location.Code, location.Address, location.IsActive, location.Version);

    private static IResult StaleVersion(long currentVersion) => ApiResults.Problem(
        StatusCodes.Status409Conflict,
        ApiErrorCodes.OptimisticConcurrencyFailure,
        "Someone saved this configuration first.",
        $"The stored version is {currentVersion}. Reload it and re-apply the change you still intend.");

    private static IResult NotFound() => ApiResults.Problem(
        StatusCodes.Status404NotFound,
        ApiErrorCodes.NotFound,
        "This record does not exist in the current business.");
}
