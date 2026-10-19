using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Catalog;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Sync;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Catalog;

/// <summary>
/// Product and variant master data (QB-06). A product always carries at least one default variant so
/// transaction lines reference concrete variants, and the master never stores stock: stock is derived from
/// inventory balances (spec Â§3.5, F-12).
/// </summary>
public static class ProductEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;
    private const int MaxNameLength = 200;
    private const int MaxSkuLength = 64;
    private const int MaxBarcodeLength = 64;
    private const int MoneyScale = 4;

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products").WithTags("Catalog");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.ProductsRead);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.ProductsRead);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.ProductsWrite);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(Permissions.ProductsWrite);
        group.MapPut("/{id:guid}/variants/{variantId:guid}", UpdateVariantAsync).RequireAuthorization(Permissions.ProductsWrite);

        return app;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? search,
        string? cursor,
        int? pageSize,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.Products.AsNoTracking().Where(product => product.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(product => product.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, pattern)
                || (product.Sku != null && EF.Functions.ILike(product.Sku, pattern))
                || db.ProductVariants.Any(variant =>
                    variant.ProductId == product.Id
                    && (EF.Functions.ILike(variant.Name, pattern)
                        || (variant.Sku != null && EF.Functions.ILike(variant.Sku, pattern))
                        || (variant.Barcode != null && EF.Functions.ILike(variant.Barcode, pattern)))));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(product => product.Id.CompareTo(cursorId) < 0);
        }

        var rows = await query
            .OrderByDescending(product => product.Id)
            .Take(size + 1)
            .Select(product => new ProductDto(
                product.Id,
                product.Name,
                product.Sku,
                product.CategoryId,
                product.UnitId,
                product.IsActive,
                product.TrackStock,
                product.Version,
                product.TaxRateId == null
                    ? 0m
                    : db.TaxRates.Where(rate => rate.Id == product.TaxRateId).Select(rate => rate.Rate).FirstOrDefault(),
                product.TaxType == TaxType.Inclusive ? "Inclusive" : "Exclusive"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        return Results.Ok(new PagedResult<ProductDto>(rows, nextCursor, size));
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var product = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return NotFound();
        }

        return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveProductRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var sku = string.IsNullOrWhiteSpace(request.Sku) ? null : request.Sku.Trim();
        var errors = ValidateProduct(name, sku);
        ValidatePrice(request.SellingPrice, request.PurchasePrice, errors);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The product payload is invalid.", errors);
        }

        if (sku is not null && await SkuTakenAsync(db, businessId, sku, null, cancellationToken).ConfigureAwait(false))
        {
            return DuplicateSku(sku);
        }

        var product = new Product
        {
            BusinessId = businessId,
            Name = name,
            Sku = sku,
            CategoryId = request.CategoryId,
            UnitId = request.UnitId,
            TrackStock = request.TrackStock,
            IsActive = true,
        };

        var variant = new ProductVariant
        {
            BusinessId = businessId,
            ProductId = product.Id,
            Name = string.IsNullOrWhiteSpace(request.VariantName) ? name : request.VariantName.Trim(),
            Sku = sku,
            SellingPrice = decimal.Round(request.SellingPrice, MoneyScale),
            PurchasePrice = request.PurchasePrice is { } purchase ? decimal.Round(purchase, MoneyScale) : null,
            IsDefault = true,
            IsActive = true,
        };

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    db.Products.Add(product);
                    db.ProductVariants.Add(variant);

                    await SyncProjections.ProductAsync(db, businessId, product, [variant], cancellationToken)
                        .ConfigureAwait(false);

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

        return Results.Created($"/api/v1/products/{product.Id}", await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var product = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return NotFound();
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var sku = string.IsNullOrWhiteSpace(request.Sku) ? null : request.Sku.Trim();
        var errors = ValidateProduct(name, sku);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The product payload is invalid.", errors);
        }

        if (sku is not null && await SkuTakenAsync(db, businessId, sku, id, cancellationToken).ConfigureAwait(false))
        {
            return DuplicateSku(sku);
        }

        var variants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await SyncTransaction.RunAsync(
            db,
            async () =>
            {
                product.Name = name;
                product.Sku = sku;
                product.CategoryId = request.CategoryId;
                product.IsActive = request.IsActive;

                await SyncProjections.ProductAsync(db, businessId, product, variants, cancellationToken)
                    .ConfigureAwait(false);

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> UpdateVariantAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        Guid id,
        Guid variantId,
        UpdateVariantRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var variant = await db.ProductVariants
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == variantId && candidate.ProductId == id, cancellationToken)
            .ConfigureAwait(false);

        if (variant is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "The variant does not exist on this product in this business.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        var errors = new Dictionary<string, string[]>();

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (barcode is { Length: > MaxBarcodeLength })
        {
            errors["barcode"] = [$"A barcode of at most {MaxBarcodeLength} characters is supported."];
        }

        ValidatePrice(request.SellingPrice, request.PurchasePrice, errors);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The variant payload is invalid.", errors);
        }

        if (barcode is not null)
        {
            var barcodeTaken = await db.ProductVariants.AnyAsync(
                candidate => candidate.BusinessId == businessId && candidate.Barcode == barcode && candidate.Id != variantId,
                cancellationToken).ConfigureAwait(false);

            if (barcodeTaken)
            {
                return ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.DuplicateSku,
                    "This barcode is already used by another variant in this business.");
            }
        }

        var product = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == variant.ProductId, cancellationToken)
            .ConfigureAwait(false);

        var siblings = product is null
            ? []
            : await db.ProductVariants.AsNoTracking()
                .Where(candidate => candidate.BusinessId == businessId && candidate.ProductId == product.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        await SyncTransaction.RunAsync(
            db,
            async () =>
            {
                variant.Name = name;
                variant.Sku = string.IsNullOrWhiteSpace(request.Sku) ? variant.Sku : request.Sku.Trim();
                variant.Barcode = barcode;
                variant.SellingPrice = decimal.Round(request.SellingPrice, MoneyScale);
                variant.PurchasePrice = request.PurchasePrice is { } purchase ? decimal.Round(purchase, MoneyScale) : null;
                variant.IsActive = request.IsActive;

                if (product is not null)
                {
                    await SyncProjections.ProductAsync(db, businessId, product, siblings, cancellationToken)
                        .ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(variant));
    }

    private static Dictionary<string, string[]> ValidateProduct(string name, string? sku)
    {
        var errors = new Dictionary<string, string[]>();

        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name of 1-{MaxNameLength} characters is required."];
        }

        if (sku is { Length: > MaxSkuLength })
        {
            errors["sku"] = [$"An SKU of at most {MaxSkuLength} characters is supported."];
        }

        return errors;
    }

    private static void ValidatePrice(decimal sellingPrice, decimal? purchasePrice, Dictionary<string, string[]> errors)
    {
        if (sellingPrice < 0)
        {
            errors["sellingPrice"] = ["The selling price must not be negative."];
        }
        else if (decimal.Round(sellingPrice, MoneyScale) != sellingPrice)
        {
            errors["sellingPrice"] = [$"Prices support at most {MoneyScale} decimal places."];
        }

        if (purchasePrice is { } purchase)
        {
            if (purchase < 0)
            {
                errors["purchasePrice"] = ["The purchase price must not be negative."];
            }
            else if (decimal.Round(purchase, MoneyScale) != purchase)
            {
                errors["purchasePrice"] = [$"Prices support at most {MoneyScale} decimal places."];
            }
        }
    }

    private static async Task<bool> SkuTakenAsync(
        BillingDbContext db,
        Guid businessId,
        string sku,
        Guid? exceptProductId,
        CancellationToken cancellationToken)
        => await db.Products.AnyAsync(
            product => product.BusinessId == businessId
                && product.Sku == sku
                && (exceptProductId == null || product.Id != exceptProductId),
            cancellationToken).ConfigureAwait(false)
            || await db.ProductVariants.AnyAsync(
                variant => variant.BusinessId == businessId
                    && variant.Sku == sku
                    && (exceptProductId == null || variant.ProductId != exceptProductId),
                cancellationToken).ConfigureAwait(false);

    private static async Task<ProductDetailDto> ToDetailAsync(
        BillingDbContext db,
        Product product,
        CancellationToken cancellationToken)
    {
        var taxRate = product.TaxRateId is { } taxRateId
            ? await db.TaxRates.AsNoTracking()
                .Where(rate => rate.Id == taxRateId)
                .Select(rate => rate.Rate)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : 0m;

        var variants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.BusinessId == product.BusinessId && variant.ProductId == product.Id)
            .OrderByDescending(variant => variant.IsDefault)
            .ThenBy(variant => variant.Name)
            .Select(variant => new ProductVariantDto(
                variant.Id,
                variant.ProductId,
                variant.Name,
                variant.Sku,
                variant.Barcode,
                variant.SellingPrice,
                variant.PurchasePrice,
                variant.IsDefault,
                variant.IsActive,
                variant.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ProductDetailDto(
            new ProductDto(
                product.Id,
                product.Name,
                product.Sku,
                product.CategoryId,
                product.UnitId,
                product.IsActive,
                product.TrackStock,
                product.Version,
                taxRate,
                product.TaxType == TaxType.Inclusive ? "Inclusive" : "Exclusive"),
            variants);
    }

    private static ProductVariantDto ToDto(ProductVariant variant)
        => new(
            variant.Id,
            variant.ProductId,
            variant.Name,
            variant.Sku,
            variant.Barcode,
            variant.SellingPrice,
            variant.PurchasePrice,
            variant.IsDefault,
            variant.IsActive,
            variant.Version);

    private static IResult NotFound()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The product does not exist in this business.");

    private static IResult DuplicateSku(string sku)
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.DuplicateSku,
            "This SKU is already used in this business.",
            $"SKU '{sku}' must be unique per business; reuse an existing product or pick another SKU.");

    private static IResult MapPostgresException(PostgresException exception)
        => exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation =>
                ApiResults.Problem(
                    StatusCodes.Status409Conflict,
                    ApiErrorCodes.DuplicateSku,
                    "The product conflicts with an existing SKU in this business."),
            PostgresErrorCodes.ForeignKeyViolation =>
                ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The product references a category or unit outside this business."),
            _ => ApiResults.Problem(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The product write failed."),
        };
}
