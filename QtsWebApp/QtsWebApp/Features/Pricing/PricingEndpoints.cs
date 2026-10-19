using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Pricing;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Accounting;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Pricing;

/// <summary>
/// Tax and discount integrity (QB-12, F-18): rates are exact decimals bounded to [0, 1] and stored with
/// numeric precision, discount definitions carry bounded values (percentage 0-100, fixed non-negative)
/// and an ordered validity window. Disallowed values are rejected before they can reach the database, and
/// every definition stays scoped to one business.
/// </summary>
public static class PricingEndpoints
{
    private const int MaxNameLength = 200;

    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var taxes = app.MapGroup("/api/v1/tax-rates").WithTags("Pricing");

        taxes.MapGet("/", ListTaxRatesAsync).RequireAuthorization(Permissions.TaxesManage);
        taxes.MapPost("/", CreateTaxRateAsync).RequireAuthorization(Permissions.TaxesManage);
        taxes.MapGet("/{id:guid}", GetTaxRateAsync).RequireAuthorization(Permissions.TaxesManage);
        taxes.MapPut("/{id:guid}", UpdateTaxRateAsync).RequireAuthorization(Permissions.TaxesManage);
        taxes.MapDelete("/{id:guid}", DeleteTaxRateAsync).RequireAuthorization(Permissions.TaxesManage);

        var discounts = app.MapGroup("/api/v1/discounts").WithTags("Pricing");

        discounts.MapGet("/", ListDiscountsAsync).RequireAuthorization(Permissions.DiscountsManage);
        discounts.MapPost("/", CreateDiscountAsync).RequireAuthorization(Permissions.DiscountsManage);
        discounts.MapGet("/{id:guid}", GetDiscountAsync).RequireAuthorization(Permissions.DiscountsManage);
        discounts.MapPut("/{id:guid}", UpdateDiscountAsync).RequireAuthorization(Permissions.DiscountsManage);
        discounts.MapDelete("/{id:guid}", DeleteDiscountAsync).RequireAuthorization(Permissions.DiscountsManage);

        return app;
    }

    // ---------------------------------------------------------------- tax rates

    private static async Task<IResult> ListTaxRatesAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var query = db.TaxRates.AsNoTracking().Where(rate => rate.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(rate => rate.IsActive);
        }

        var rows = await query
            .OrderBy(rate => rate.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows.Select(ToTaxRateDto).ToList());
    }

    private static async Task<IResult> CreateTaxRateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveTaxRateRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var errors = ValidateTaxRate(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The tax rate payload is invalid.", errors);
        }

        var name = request.Name.Trim();
        var duplicate = await db.TaxRates.AsNoTracking()
            .AnyAsync(rate => rate.BusinessId == businessId && rate.Name == name, cancellationToken)
            .ConfigureAwait(false);

        if (duplicate)
        {
            return DuplicateName();
        }

        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var rate = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                if (request.IsDefault)
                {
                    await ClearDefaultsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
                }

                var rate = new TaxRate
                {
                    BusinessId = businessId,
                    Name = name,
                    Rate = decimal.Round(request.Rate, 6, MidpointRounding.AwayFromZero),
                    IsDefault = request.IsDefault,
                    IsActive = request.IsActive,
                };

                db.TaxRates.Add(rate);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return rate;
            }).ConfigureAwait(false);

            return Results.Created($"/api/v1/tax-rates/{rate.Id}", ToTaxRateDto(rate));
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }
    }

    private static async Task<IResult> GetTaxRateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var rate = await db.TaxRates.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return rate is null ? NotFoundTaxRate() : Results.Ok(ToTaxRateDto(rate));
    }

    private static async Task<IResult> UpdateTaxRateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        SaveTaxRateRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var errors = ValidateTaxRate(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The tax rate payload is invalid.", errors);
        }

        var name = request.Name.Trim();
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            var rate = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var rate = await db.TaxRates
                    .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                if (rate is null)
                {
                    return null;
                }

                var duplicate = await db.TaxRates.AsNoTracking()
                    .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id != id && candidate.Name == name, cancellationToken)
                    .ConfigureAwait(false);

                if (duplicate)
                {
                    throw new DomainRuleException(
                        StatusCodes.Status409Conflict,
                        ApiErrorCodes.DuplicateName,
                        "This name is already used in this business.",
                        "Names are unique per business; reuse the existing record or pick another name.");
                }

                if (request.IsDefault)
                {
                    await ClearDefaultsAsync(db, businessId, cancellationToken).ConfigureAwait(false);
                }

                rate.Name = name;
                rate.Rate = decimal.Round(request.Rate, 6, MidpointRounding.AwayFromZero);
                rate.IsDefault = request.IsDefault;
                rate.IsActive = request.IsActive;

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return rate;
            }).ConfigureAwait(false);

            if (rate is null)
            {
                return NotFoundTaxRate();
            }

            return Results.Ok(ToTaxRateDto(rate));
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

    private static async Task<IResult> DeleteTaxRateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var rate = await db.TaxRates
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (rate is null)
        {
            return NotFoundTaxRate();
        }

        // Soft delete keeps historical documents that referenced the rate legible (D-29 pattern).
        rate.IsActive = false;
        rate.IsDefault = false;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static Dictionary<string, string[]> ValidateTaxRate(SaveTaxRateRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (request.Rate < 0 || request.Rate > 1)
        {
            errors["rate"] = ["The rate is a fraction between 0 and 1 (0.14 = 14%). Out-of-range rates are refused."];
        }
        else if (decimal.Round(request.Rate, 6) != request.Rate)
        {
            errors["rate"] = ["The rate supports at most 6 decimal places."];
        }

        return errors;
    }

    private static async Task ClearDefaultsAsync(BillingDbContext db, Guid businessId, CancellationToken cancellationToken)
        => await db.TaxRates
            .Where(rate => rate.BusinessId == businessId && rate.IsDefault)
            .ExecuteUpdateAsync(update => update.SetProperty(rate => rate.IsDefault, false), cancellationToken)
            .ConfigureAwait(false);

    private static TaxRateDto ToTaxRateDto(TaxRate rate)
        => new(
            rate.Id,
            rate.Name,
            rate.Rate,
            rate.IsDefault,
            rate.IsActive,
            rate.CreatedAtUtc,
            rate.UpdatedAtUtc);

    // ---------------------------------------------------------------- discounts

    private static async Task<IResult> ListDiscountsAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        bool? includeInactive,
        string? scope,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var query = db.Discounts.AsNoTracking().Where(discount => discount.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(discount => discount.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            if (!Enum.TryParse<DiscountAppliesTo>(scope.Trim(), ignoreCase: true, out var parsed))
            {
                return ApiResults.ValidationProblem(
                    "The filter is invalid.",
                    new Dictionary<string, string[]> { ["scope"] = [$"Unknown discount scope '{scope}'."] });
            }

            query = query.Where(discount => discount.AppliesTo == parsed);
        }

        var rows = await query
            .OrderBy(discount => discount.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows.Select(ToDiscountDto).ToList());
    }

    private static async Task<IResult> CreateDiscountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveDiscountRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var parsed = ValidateDiscount(request);

        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var name = request.Name.Trim();
        var duplicate = await db.Discounts.AsNoTracking()
            .AnyAsync(discount => discount.BusinessId == businessId && discount.Name == name, cancellationToken)
            .ConfigureAwait(false);

        if (duplicate)
        {
            return DuplicateName();
        }

        var discount = new Discount
        {
            BusinessId = businessId,
            Name = name,
            Type = parsed.Kind!.Value,
            Value = parsed.Value!.Value,
            AppliesTo = parsed.Scope!.Value,
            StartsAtUtc = request.StartsAtUtc,
            EndsAtUtc = request.EndsAtUtc,
            IsActive = request.IsActive,
        };

        db.Discounts.Add(discount);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

        return Results.Created($"/api/v1/discounts/{discount.Id}", ToDiscountDto(discount));
    }

    private static async Task<IResult> GetDiscountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var discount = await db.Discounts.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return discount is null ? NotFoundDiscount() : Results.Ok(ToDiscountDto(discount));
    }

    private static async Task<IResult> UpdateDiscountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        SaveDiscountRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var parsed = ValidateDiscount(request);

        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var discount = await db.Discounts
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (discount is null)
        {
            return NotFoundDiscount();
        }

        var name = request.Name.Trim();
        var duplicate = await db.Discounts.AsNoTracking()
            .AnyAsync(candidate => candidate.BusinessId == businessId && candidate.Id != id && candidate.Name == name, cancellationToken)
            .ConfigureAwait(false);

        if (duplicate)
        {
            return DuplicateName();
        }

        discount.Name = name;
        discount.Type = parsed.Kind!.Value;
        discount.Value = parsed.Value!.Value;
        discount.AppliesTo = parsed.Scope!.Value;
        discount.StartsAtUtc = request.StartsAtUtc;
        discount.EndsAtUtc = request.EndsAtUtc;
        discount.IsActive = request.IsActive;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

        return Results.Ok(ToDiscountDto(discount));
    }

    private static async Task<IResult> DeleteDiscountAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var discount = await db.Discounts
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (discount is null)
        {
            return NotFoundDiscount();
        }

        discount.IsActive = false;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static ParsedDiscount ValidateDiscount(SaveDiscountRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request?.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        var kind = default(DiscountType);
        var kindValid = Enum.TryParse(request?.Kind?.Trim(), ignoreCase: true, out kind);

        if (!kindValid)
        {
            errors["kind"] = ["Use 'Fixed' or 'Percentage'."];
        }

        var value = request?.Value ?? 0m;

        if (kindValid && kind == DiscountType.Percentage && (value < 0 || value > 100))
        {
            errors["value"] = ["A percentage discount is between 0 and 100. Disallowed discounts are refused."];
        }
        else if (kindValid && kind == DiscountType.Fixed && value < 0)
        {
            errors["value"] = ["A fixed discount cannot be negative."];
        }
        else if (decimal.Round(value, 4) != value)
        {
            errors["value"] = ["The discount value supports at most 4 decimal places."];
        }

        var scope = default(DiscountAppliesTo);

        if (!Enum.TryParse(request?.AppliesTo?.Trim(), ignoreCase: true, out scope))
        {
            errors["appliesTo"] = ["Use 'All', 'Product', 'Category' or 'Customer'."];
        }

        if (request?.StartsAtUtc is { } start && request.EndsAtUtc is { } end && end <= start)
        {
            errors["endsAtUtc"] = ["The discount window must end after it starts."];
        }

        if (errors.Count > 0)
        {
            return new ParsedDiscount(
                ApiResults.ValidationProblem("The discount payload is invalid.", errors),
                null,
                null,
                null);
        }

        return new ParsedDiscount(null, kind, decimal.Round(value, 4), scope);
    }

    private static DiscountDto ToDiscountDto(Discount discount)
        => new(
            discount.Id,
            discount.Name,
            EnumBridge.To<DiscountKind>(discount.Type),
            discount.Value,
            EnumBridge.To<DiscountScope>(discount.AppliesTo),
            discount.StartsAtUtc,
            discount.EndsAtUtc,
            discount.IsActive,
            discount.CreatedAtUtc,
            discount.UpdatedAtUtc);

    // ---------------------------------------------------------------- shared helpers

    private static IResult DuplicateName()
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.DuplicateName,
            "This name is already used in this business.",
            "Names are unique per business; reuse the existing record or pick another name.");

    private static IResult NotFoundTaxRate()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The tax rate does not exist in this business.");

    private static IResult NotFoundDiscount()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The discount does not exist in this business.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.DuplicateName,
                    "This name is already used in this business."),
            PostgresErrorCodes.CheckViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.Conflict,
                    "The value violates a pricing guard (rate fraction or discount range)."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The pricing operation failed."),
        };

    private sealed record ParsedDiscount(IResult? Error, DiscountType? Kind, decimal? Value, DiscountAppliesTo? Scope);
}
