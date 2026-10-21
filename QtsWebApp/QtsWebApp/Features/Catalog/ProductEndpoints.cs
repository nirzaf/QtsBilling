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
    private const int MaxReasonLength = 500;
    private const int MoneyScale = 4;

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products").WithTags("Catalog");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.ProductsRead);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.ProductsRead);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.ProductsWrite);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(Permissions.ProductsWrite);

        // A product grows variants; a variant is corrected in place and retired on its own route, so the two
        // operations that change what a counter can sell are visible separately in the audit trail.
        group.MapPost("/{id:guid}/variants", CreateVariantAsync).RequireAuthorization(Permissions.ProductsWrite);
        group.MapPut("/{id:guid}/variants/{variantId:guid}", UpdateVariantAsync).RequireAuthorization(Permissions.ProductsWrite);
        group.MapPost("/{id:guid}/variants/{variantId:guid}/archive", ArchiveVariantAsync).RequireAuthorization(Permissions.ProductsWrite);

        // Archive and restore are not fields of a general update: each one states why, and emits the projection or
        // tombstone the devices need in the same transaction as the row change.
        group.MapPost("/{id:guid}/archive", ArchiveProductAsync).RequireAuthorization(Permissions.ProductsWrite);
        group.MapPost("/{id:guid}/restore", RestoreProductAsync).RequireAuthorization(Permissions.ProductsWrite);

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

        if (!TryReadTaxType(request.TaxType, out var taxType))
        {
            errors["taxType"] = ["Use 'Inclusive' or 'Exclusive' for how tax sits against the price."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The product payload is invalid.", errors);
        }

        var referenceErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await ValidateReferencesAsync(
            db, businessId, request.CategoryId, request.UnitId, request.BrandId, request.ProductGroupId, request.TaxRateId,
            referenceErrors, cancellationToken).ConfigureAwait(false);

        if (referenceErrors.Count > 0)
        {
            return ApiResults.ValidationProblem("The product references something unavailable.", referenceErrors);
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
            BrandId = request.BrandId,
            ProductGroupId = request.ProductGroupId,
            UnitId = request.UnitId,
            TaxRateId = request.TaxRateId,
            TaxType = taxType,
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

                    await SyncProjections.ProductAsync(db, businessId, product, [variant], cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, product.Id, "created", new
                    {
                        name = product.Name,
                        sku = product.Sku,
                        unitId = product.UnitId,
                        categoryId = product.CategoryId,
                        trackStock = product.TrackStock,
                        defaultVariantId = variant.Id,
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException wrapper) when (wrapper.InnerException is PostgresException collided)
        {
            // EF surfaces a rejected unique index as DbUpdateException, so the collision is mapped from the
            // database error the wrapper carries rather than reported as an unexplained failure.
            return MapPostgresException(collided);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

        return Results.Created($"/api/v1/products/{product.Id}", await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> CreateVariantAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        CreateProductVariantRequest request,
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
        var barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        var errors = ValidateProduct(name, sku);

        if (barcode is { Length: > MaxBarcodeLength })
        {
            errors["barcode"] = [$"A barcode of at most {MaxBarcodeLength} characters is supported."];
        }

        ValidatePrice(request.SellingPrice, request.PurchasePrice, errors);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The variant payload is invalid.", errors);
        }

        // The version the caller read is the version this write is against. A retried or slow create therefore
        // meets a refusal instead of adding a second variant nobody asked for.
        if (request.ExpectedProductVersion != product.Version)
        {
            return StaleVersion(product.Version, "variant");
        }

        if (!product.IsActive)
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This product is out of service.",
                "Restore the product first; a variant is not added to a record the counter cannot sell from.");
        }

        if (sku is not null && await VariantSkuTakenAsync(db, businessId, sku, product.Id, null, cancellationToken).ConfigureAwait(false))
        {
            return DuplicateSku(sku);
        }

        if (barcode is not null && await db.ProductVariants.AnyAsync(
            candidate => candidate.BusinessId == businessId && candidate.Barcode == barcode,
            cancellationToken).ConfigureAwait(false))
        {
            return DuplicateBarcode(barcode);
        }

        var siblings = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var variant = new ProductVariant
        {
            BusinessId = businessId,
            ProductId = product.Id,
            Name = name,
            Sku = sku,
            Barcode = barcode,
            SellingPrice = decimal.Round(request.SellingPrice, MoneyScale),
            PurchasePrice = request.PurchasePrice is { } purchase ? decimal.Round(purchase, MoneyScale) : null,
            // A new variant is never the default by accident: the default carries the product's own identity, and
            // promoting one is the operator's decision.
            IsDefault = false,
            IsActive = true,
        };

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    db.ProductVariants.Add(variant);

                    // Touching the product row advances the version every variant edit is checked against, and
                    // makes this transaction the one that holds the row lock while the variant list changes.
                    product.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await SyncProjections.ProductAsync(
                        db, businessId, product, [.. siblings, variant], cancellationToken: cancellationToken).ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, product.Id, "variant_created", new
                    {
                        variantId = variant.Id,
                        name = variant.Name,
                        sku = variant.Sku,
                        hasBarcode = barcode is not null,
                        sellingPrice = variant.SellingPrice,
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException wrapper) when (wrapper.InnerException is PostgresException collided)
        {
            // EF surfaces a rejected unique index as DbUpdateException, so the collision is mapped from the
            // database error the wrapper carries rather than reported as an unexplained failure.
            return MapPostgresException(collided);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

        return Results.Created($"/api/v1/products/{product.Id}/variants/{variant.Id}", await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
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

        if (!TryReadTaxType(request.TaxType, out var taxType))
        {
            errors["taxType"] = ["Use 'Inclusive' or 'Exclusive' for how tax sits against the price."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The product payload is invalid.", errors);
        }

        if (request.ExpectedVersion != product.Version)
        {
            return StaleVersion(product.Version, "product");
        }

        var referenceErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await ValidateReferencesAsync(
            db, businessId, request.CategoryId, request.UnitId, request.BrandId, request.ProductGroupId, request.TaxRateId,
            referenceErrors, cancellationToken).ConfigureAwait(false);

        if (referenceErrors.Count > 0)
        {
            return ApiResults.ValidationProblem("The product references something unavailable.", referenceErrors);
        }

        if (sku is not null && await SkuTakenAsync(db, businessId, sku, id, cancellationToken).ConfigureAwait(false))
        {
            return DuplicateSku(sku);
        }

        // A quantity already booked is expressed in the unit the movement carried. Changing it afterwards would
        // leave history readable in one unit and the master in another, so it takes a separately controlled
        // conversion (specification task 17, recipe step 2).
        if (request.UnitId != product.UnitId
            && await HasMovementsAsync(db, businessId, product.Id, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.ApprovalRequired,
                "Stock has already moved for this product, so its unit cannot be changed here.",
                "Quantities already booked are stated in the old unit. Use a controlled conversion or a stock count, never a silent redefinition.");
        }

        var variants = await db.ProductVariants
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var previousUnitId = product.UnitId;

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    product.Name = name;
                    product.Sku = sku;
                    product.CategoryId = request.CategoryId;
                    product.UnitId = request.UnitId;
                    product.BrandId = request.BrandId;
                    product.ProductGroupId = request.ProductGroupId;
                    product.TaxRateId = request.TaxRateId;
                    product.TaxType = taxType;

                    await SyncProjections.ProductAsync(db, businessId, product, variants, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, product.Id, "updated", new
                    {
                        name = product.Name,
                        sku = product.Sku,
                        unitChanged = previousUnitId != product.UnitId,
                        categoryId = product.CategoryId,
                        version = product.Version + 1,
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(product.Version, "product");
        }
        catch (DbUpdateException wrapper) when (wrapper.InnerException is PostgresException collided)
        {
            // EF surfaces a rejected unique index as DbUpdateException, so the collision is mapped from the
            // database error the wrapper carries rather than reported as an unexplained failure.
            return MapPostgresException(collided);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

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

        var owner = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == variant.ProductId, cancellationToken)
            .ConfigureAwait(false);

        if (owner is null)
        {
            return NotFound();
        }

        if (request.ExpectedProductVersion != owner.Version)
        {
            return StaleVersion(owner.Version, "variant");
        }

        var sku = string.IsNullOrWhiteSpace(request.Sku) ? null : request.Sku.Trim();

        if (sku is not null && await VariantSkuTakenAsync(db, businessId, sku, variant.ProductId, variant.Id, cancellationToken).ConfigureAwait(false))
        {
            return DuplicateSku(sku);
        }

        if (barcode is not null)
        {
            var barcodeTaken = await db.ProductVariants.AnyAsync(
                candidate => candidate.BusinessId == businessId && candidate.Barcode == barcode && candidate.Id != variantId,
                cancellationToken).ConfigureAwait(false);

            if (barcodeTaken)
            {
                return DuplicateBarcode(barcode);
            }
        }

        var siblings = await db.ProductVariants.AsNoTracking()
            .Where(candidate => candidate.BusinessId == businessId && candidate.ProductId == owner.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    variant.Name = name;
                    variant.Sku = sku ?? variant.Sku;
                    variant.Barcode = barcode;
                    variant.SellingPrice = decimal.Round(request.SellingPrice, MoneyScale);
                    variant.PurchasePrice = request.PurchasePrice is { } purchase ? decimal.Round(purchase, MoneyScale) : null;

                    // A price change is forward-looking: documents already issued carry the snapshot they were
                    // priced with, and the projection this write emits is what a device caches from now on.
                    owner.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await SyncProjections.ProductAsync(db, businessId, owner, siblings, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, owner.Id, "variant_updated", new
                    {
                        variantId = variant.Id,
                        name = variant.Name,
                        sku = variant.Sku,
                        sellingPrice = variant.SellingPrice,
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(owner.Version, "variant");
        }
        catch (DbUpdateException wrapper) when (wrapper.InnerException is PostgresException collided)
        {
            // EF surfaces a rejected unique index as DbUpdateException, so the collision is mapped from the
            // database error the wrapper carries rather than reported as an unexplained failure.
            return MapPostgresException(collided);
        }
        catch (PostgresException exception)
        {
            return MapPostgresException(exception);
        }

        return Results.Ok(ToDto(variant));
    }

    private static async Task<IResult> ArchiveVariantAsync(
        Guid id,
        Guid variantId,
        ClaimsPrincipal principal,
        BillingDbContext db,
        ArchiveVariantRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem("A reason is required.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = [$"Record why this variant stops being sold (1-{MaxReasonLength} characters)."],
            });
        }

        var product = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return NotFound();
        }

        var siblings = await db.ProductVariants
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var variant = siblings.FirstOrDefault(candidate => candidate.Id == variantId);
        if (variant is null)
        {
            return ApiResults.Problem(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "The variant does not exist on this product in this business.");
        }

        if (request.ExpectedProductVersion != product.Version)
        {
            return StaleVersion(product.Version, "variant");
        }

        // Exactly one active default: retiring the variant that carries it must hand the role to a named active
        // sibling rather than leave a product whose default nobody can sell.
        ProductVariant? successor = null;

        if (variant.IsDefault && variant.IsActive)
        {
            if (request.NewDefaultVariantId is not { } successorId)
            {
                return ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "This is the product's active default variant.",
                    "Name the active variant that should become the default; a product cannot be left without one a counter can sell.");
            }

            successor = siblings.FirstOrDefault(candidate => candidate.Id == successorId && candidate.IsActive);

            if (successor is null || successor.Id == variant.Id)
            {
                return ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The new default must be another active variant of this product.",
                    "Choose from the variants listed for this product; a retired or foreign variant cannot become the default.");
            }
        }

        if (variant.IsActive == false)
        {
            // Retiring twice is not an event; the first record already carries the reason.
            return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    variant.IsActive = false;

                    if (successor is not null)
                    {
                        variant.IsDefault = false;
                        successor.IsDefault = true;
                    }

                    product.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await SyncProjections.ProductAsync(db, businessId, product, siblings, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, product.Id, "variant_archived", new
                    {
                        variantId = variant.Id,
                        name = variant.Name,
                        reason,
                        newDefaultVariantId = successor?.Id,
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(product.Version, "variant");
        }

        return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ArchiveProductAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        ArchiveProductRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem("A reason is required.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = [$"Record why this product stops being sold (1-{MaxReasonLength} characters)."],
            });
        }

        var product = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return NotFound();
        }

        if (request.ExpectedVersion != product.Version)
        {
            return StaleVersion(product.Version, "product");
        }

        var variants = await db.ProductVariants
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!product.IsActive)
        {
            return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    product.IsActive = false;

                    foreach (var variant in variants)
                    {
                        variant.IsActive = false;
                    }

                    // A device drops the cached price list rather than keeping a product it can still offer, which
                    // is the same tombstone rule a deactivated customer follows (specification D04/task 35).
                    await SyncProjections.ProductAsync(
                        db, businessId, product, variants, tombstone: true, cancellationToken: cancellationToken).ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, product.Id, "archived", new
                    {
                        name = product.Name,
                        reason,
                        variantsRetired = variants.Count(variant => variant.IsActive),
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(product.Version, "product");
        }

        return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> RestoreProductAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        RestoreProductRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxReasonLength)
        {
            return ApiResults.ValidationProblem("A reason is required.", new Dictionary<string, string[]>
            {
                [nameof(request.Reason)] = [$"Record why this product is sold again (1-{MaxReasonLength} characters)."],
            });
        }

        var product = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.BusinessId == businessId && candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return NotFound();
        }

        if (request.ExpectedVersion != product.Version)
        {
            return StaleVersion(product.Version, "product");
        }

        var variants = await db.ProductVariants
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (product.IsActive)
        {
            return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            await SyncTransaction.RunAsync(
                db,
                async () =>
                {
                    // Bringing the product back does not resurrect variants somebody retired on purpose: the default
                    // is restored only if none is active, so the counter has something to sell and no price is invented.
                    product.IsActive = true;

                    if (variants.All(variant => !variant.IsActive) && variants.Count > 0)
                    {
                        var promoted = variants.FirstOrDefault(variant => variant.IsDefault) ?? variants[0];
                        promoted.IsActive = true;
                    }

                    await SyncProjections.ProductAsync(db, businessId, product, variants, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    CatalogAudit(db, businessId, principal, product.Id, "restored", new
                    {
                        name = product.Name,
                        reason,
                        activeVariants = variants.Count(variant => variant.IsActive),
                    });

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion(product.Version, "product");
        }

        return Results.Ok(await ToDetailAsync(db, product, cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// A variant SKU must be free across this business's products and variants - the same rule a product SKU
    /// follows, stated by this check and decided by a database index. <paramref name="exceptVariantId"/> lets an
    /// edit keep its own SKU without colliding with itself, and <paramref name="exceptProductId"/> the product's own
    /// SKU, which its default variant already carries.
    /// </summary>
    private static async Task<bool> VariantSkuTakenAsync(
        BillingDbContext db,
        Guid businessId,
        string sku,
        Guid? exceptProductId,
        Guid? exceptVariantId,
        CancellationToken cancellationToken)
        => await db.Products.AnyAsync(
            product => product.BusinessId == businessId
                && product.Sku == sku
                && (exceptProductId == null || product.Id != exceptProductId),
            cancellationToken).ConfigureAwait(false)
            || await db.ProductVariants.AnyAsync(
                variant => variant.BusinessId == businessId
                    && variant.Sku == sku
                    && (exceptVariantId == null || variant.Id != exceptVariantId),
                cancellationToken).ConfigureAwait(false);

    /// <summary>True once any quantity of this product's variants has moved; movements are append-only history.</summary>
    private static async Task<bool> HasMovementsAsync(
        BillingDbContext db,
        Guid businessId,
        Guid productId,
        CancellationToken cancellationToken)
    {
        var variantIds = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.BusinessId == businessId && variant.ProductId == productId)
            .Select(variant => variant.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return variantIds.Count > 0
            && await db.InventoryMovements.AsNoTracking().AnyAsync(
                movement => movement.BusinessId == businessId && variantIds.Contains(movement.VariantId),
                cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// A reference may be null, but never another business's record or one that has been retired: a product filed
    /// under a closed category or sold in a unit that no longer exists is a data-quality surprise nobody asked for.
    /// </summary>
    private static async Task ValidateReferencesAsync(
        BillingDbContext db,
        Guid businessId,
        Guid? categoryId,
        Guid? unitId,
        Guid? brandId,
        Guid? productGroupId,
        Guid? taxRateId,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (categoryId is { } category
            && !await db.Categories.AnyAsync(
                row => row.BusinessId == businessId && row.Id == category && row.IsActive,
                cancellationToken).ConfigureAwait(false))
        {
            errors[nameof(categoryId)] = ["Choose an active category of this business."];
        }

        if (unitId is { } unit
            && !await db.Units.AnyAsync(
                row => row.BusinessId == businessId && row.Id == unit,
                cancellationToken).ConfigureAwait(false))
        {
            errors[nameof(unitId)] = ["Choose a unit of measure of this business."];
        }

        if (brandId is { } brand
            && !await db.Brands.AnyAsync(
                row => row.BusinessId == businessId && row.Id == brand && row.IsActive,
                cancellationToken).ConfigureAwait(false))
        {
            errors[nameof(brandId)] = ["Choose an active brand of this business."];
        }

        if (productGroupId is { } group
            && !await db.ProductGroups.AnyAsync(
                row => row.BusinessId == businessId && row.Id == group && row.IsActive,
                cancellationToken).ConfigureAwait(false))
        {
            errors[nameof(productGroupId)] = ["Choose an active product group of this business."];
        }

        if (taxRateId is { } taxRate
            && !await db.TaxRates.AnyAsync(
                row => row.BusinessId == businessId && row.Id == taxRate && row.IsActive,
                cancellationToken).ConfigureAwait(false))
        {
            errors[nameof(taxRateId)] = ["Choose an active tax rate of this business, or none and price without tax."];
        }
    }

    private static bool TryReadTaxType(string? value, out TaxType taxType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            taxType = TaxType.Exclusive;
            return true;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: false, out taxType);
    }

    /// <summary>
    /// The catalogue trail: who changed which record, with a bounded summary that names values but never secret
    /// material. It is added inside the same transaction as the row change, so an audit row cannot describe a
    /// write that did not commit (specification task 17, recipe step 4).
    /// </summary>
    private static void CatalogAudit(
        BillingDbContext db,
        Guid businessId,
        ClaimsPrincipal principal,
        Guid productId,
        string action,
        object summary)
    {
        _ = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);

        db.AuditEvents.Add(new AuditEvent
        {
            BusinessId = businessId,
            UserId = userId == Guid.Empty ? null : userId,
            EntityType = "product",
            EntityId = productId,
            Action = action,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Summary = System.Text.Json.JsonSerializer.Serialize(summary),
        });
    }

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

    private static IResult DuplicateBarcode(string barcode)
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.Conflict,
            "This barcode is already used by another variant in this business.",
            $"Barcode '{barcode}' identifies one sellable thing; scan the existing variant instead of adding a second.");

    /// <summary>A stale version is its own answer, naming what is stored so the screen can reload and re-apply.</summary>
    private static IResult StaleVersion(long storedVersion, string what)
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.OptimisticConcurrencyFailure,
            $"Someone changed this {what} first.",
            $"The stored version is {storedVersion}. Reload the product and repeat the change you still intend.");

    private static IResult MapPostgresException(PostgresException exception)
    {
        if (exception.SqlState != PostgresErrorCodes.UniqueViolation)
        {
            return exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
                ? ApiResults.Problem(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.ValidationFailed,
                    "The product references a category or unit outside this business.")
                : ApiResults.Problem(
                    StatusCodes.Status500InternalServerError,
                    ApiErrorCodes.InternalError,
                    "The product write failed.");
        }

        // The unique indexes are the backstop: two concurrent writes that both passed their pre-check leave
        // exactly one row, and the loser reads a conflict that names what collided.
        var constraint = exception.ConstraintName ?? string.Empty;

        if (constraint.Contains("barcode", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This barcode is already used by another variant in this business.");
        }

        if (constraint.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                "This product already has a variant with that name.",
                "A variant is identified within its product by name; give it a name the counter can tell apart.");
        }

        return ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.DuplicateSku,
            "The product conflicts with an existing SKU in this business.");
    }
}
