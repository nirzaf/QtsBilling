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

/// <summary>Business-scoped CRUD for catalog units, categories, brands and product groups.</summary>
public static class MasterDataEndpoints
{
    private const int MaxNameLength = 200;

    private static readonly ResourceSpec[] Resources =
    [
        new("units", "unit", "unit"),
        new("categories", "category", "category"),
        new("brands", "brand", "brand"),
        new("product-groups", "productGroup", "product_group"),
    ];

    public static IEndpointRouteBuilder MapMasterDataEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var resource in Resources)
        {
            app.MapGet($"/api/v1/{resource.Route}", async (
                    ClaimsPrincipal principal,
                    BillingDbContext db,
                    bool? includeInactive,
                    CancellationToken cancellationToken) =>
                await ListAsync(resource, principal, db, includeInactive, cancellationToken))
                .WithTags("Catalog")
                .RequireAuthorization(Permissions.ProductsRead);

            app.MapPost($"/api/v1/{resource.Route}", async (
                    ClaimsPrincipal principal,
                    BillingDbContext db,
                    SaveMasterDataRequest request,
                    CancellationToken cancellationToken) =>
                await CreateAsync(resource, principal, db, request, cancellationToken))
                .WithTags("Catalog")
                .RequireAuthorization(Permissions.ProductsWrite);

            app.MapPut($"/api/v1/{resource.Route}/{{id:guid}}", async (
                    Guid id,
                    ClaimsPrincipal principal,
                    BillingDbContext db,
                    SaveMasterDataRequest request,
                    CancellationToken cancellationToken) =>
                await UpdateAsync(resource, id, principal, db, request, cancellationToken))
                .WithTags("Catalog")
                .RequireAuthorization(Permissions.ProductsWrite);

            app.MapDelete($"/api/v1/{resource.Route}/{{id:guid}}", async (
                    Guid id,
                    ClaimsPrincipal principal,
                    BillingDbContext db,
                    CancellationToken cancellationToken) =>
                await DeleteAsync(resource, id, principal, db, cancellationToken))
                .WithTags("Catalog")
                .RequireAuthorization(Permissions.ProductsWrite);
        }

        return app;
    }

    private static async Task<IResult> ListAsync(
        ResourceSpec resource,
        ClaimsPrincipal principal,
        BillingDbContext db,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        return resource.Kind switch
        {
            "unit" => Results.Ok(await db.Units.AsNoTracking()
                .Where(item => item.BusinessId == businessId)
                .OrderBy(item => item.Name)
                .Select(item => new MasterDataDto(item.Id, item.Name, item.ShortName, item.AllowDecimals, null, null, item.Version))
                .ToListAsync(cancellationToken)),
            "category" => Results.Ok(await db.Categories.AsNoTracking()
                .Where(item => item.BusinessId == businessId && (includeInactive == true || item.IsActive))
                .OrderBy(item => item.Name)
                .Select(item => new MasterDataDto(item.Id, item.Name, null, null, item.ParentId, item.IsActive, item.Version))
                .ToListAsync(cancellationToken)),
            "brand" => Results.Ok(await db.Brands.AsNoTracking()
                .Where(item => item.BusinessId == businessId && (includeInactive == true || item.IsActive))
                .OrderBy(item => item.Name)
                .Select(item => new MasterDataDto(item.Id, item.Name, null, null, null, item.IsActive, item.Version))
                .ToListAsync(cancellationToken)),
            "productGroup" => Results.Ok(await db.ProductGroups.AsNoTracking()
                .Where(item => item.BusinessId == businessId && (includeInactive == true || item.IsActive))
                .OrderBy(item => item.Name)
                .Select(item => new MasterDataDto(item.Id, item.Name, null, null, null, item.IsActive, item.Version))
                .ToListAsync(cancellationToken)),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> CreateAsync(
        ResourceSpec resource,
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveMasterDataRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var validation = Validate(resource, request);
        if (validation is not null)
        {
            return validation;
        }
        if (resource.Kind == "category" && request.ParentId is { } parentId
            && !await db.Categories.AnyAsync(category => category.BusinessId == businessId && category.Id == parentId && category.IsActive, cancellationToken))
        {
            return ApiResults.ValidationProblem("The category parent is unavailable.", new Dictionary<string, string[]>
            {
                [nameof(request.ParentId)] = ["Choose an active category in this business."],
            });
        }

        try
        {
            return resource.Kind switch
            {
                "unit" => await CreateUnitAsync(resource, businessId, db, request, cancellationToken),
                "category" => await CreateCategoryAsync(resource, businessId, db, request, cancellationToken),
                "brand" => await CreateBrandAsync(resource, businessId, db, request, cancellationToken),
                "productGroup" => await CreateProductGroupAsync(resource, businessId, db, request, cancellationToken),
                _ => Results.NotFound(),
            };
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return DuplicateName();
        }
    }

    private static async Task<IResult> CreateUnitAsync(ResourceSpec resource, Guid businessId, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = new Unit { BusinessId = businessId, Name = request.Name.Trim(), ShortName = request.ShortName!.Trim(), AllowDecimals = request.AllowDecimals ?? true, Version = 1 };
        await SyncTransaction.RunAsync(db, async () =>
        {
            db.Units.Add(item);
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version), false, cancellationToken);
            return true;
        }, cancellationToken);
        return Results.Created($"/api/v1/{resource.Route}/{item.Id}", ToDto(item));
    }

    private static async Task<IResult> CreateCategoryAsync(ResourceSpec resource, Guid businessId, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = new Category { BusinessId = businessId, Name = request.Name.Trim(), ParentId = request.ParentId, IsActive = true, Version = 1 };
        await SyncTransaction.RunAsync(db, async () =>
        {
            db.Categories.Add(item);
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version), false, cancellationToken);
            return true;
        }, cancellationToken);
        return Results.Created($"/api/v1/{resource.Route}/{item.Id}", ToDto(item));
    }

    private static async Task<IResult> CreateBrandAsync(ResourceSpec resource, Guid businessId, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = new Brand { BusinessId = businessId, Name = request.Name.Trim(), IsActive = true, Version = 1 };
        await SyncTransaction.RunAsync(db, async () =>
        {
            db.Brands.Add(item);
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version), false, cancellationToken);
            return true;
        }, cancellationToken);
        return Results.Created($"/api/v1/{resource.Route}/{item.Id}", ToDto(item));
    }

    private static async Task<IResult> CreateProductGroupAsync(ResourceSpec resource, Guid businessId, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = new ProductGroup { BusinessId = businessId, Name = request.Name.Trim(), IsActive = true, Version = 1 };
        await SyncTransaction.RunAsync(db, async () =>
        {
            db.ProductGroups.Add(item);
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version), false, cancellationToken);
            return true;
        }, cancellationToken);
        return Results.Created($"/api/v1/{resource.Route}/{item.Id}", ToDto(item));
    }

    private static async Task<IResult> UpdateAsync(
        ResourceSpec resource,
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveMasterDataRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }
        var validation = Validate(resource, request);
        if (validation is not null)
        {
            return validation;
        }
        if (resource.Kind == "category" && request.ParentId == id)
        {
            return ApiResults.ValidationProblem("A category cannot be its own parent.", new Dictionary<string, string[]>
            {
                [nameof(request.ParentId)] = ["Choose a different parent category."],
            });
        }
        if (resource.Kind == "category" && request.ParentId is { } parentId
            && !await IsValidCategoryParentAsync(db, businessId, id, parentId, cancellationToken))
        {
            return ApiResults.ValidationProblem("The category parent would create an invalid hierarchy.", new Dictionary<string, string[]>
            {
                [nameof(request.ParentId)] = ["Choose an active category in this business that is not a descendant of this category."],
            });
        }

        try
        {
            return resource.Kind switch
            {
                "unit" => await UpdateUnitAsync(resource, businessId, id, db, request, cancellationToken),
                "category" => await UpdateCategoryAsync(resource, businessId, id, db, request, cancellationToken),
                "brand" => await UpdateBrandAsync(resource, businessId, id, db, request, cancellationToken),
                "productGroup" => await UpdateProductGroupAsync(resource, businessId, id, db, request, cancellationToken),
                _ => Results.NotFound(),
            };
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return DuplicateName();
        }
    }

    private static async Task<IResult> UpdateUnitAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = await db.Units.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        item.Name = request.Name.Trim();
        item.ShortName = request.ShortName!.Trim();
        item.AllowDecimals = request.AllowDecimals ?? item.AllowDecimals;
        await SyncTransaction.RunAsync(db, async () =>
        {
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version + 1), false, cancellationToken);
            return item;
        }, cancellationToken);
        return Results.Ok(ToDto(item));
    }

    private static async Task<IResult> UpdateCategoryAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = await db.Categories.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        item.Name = request.Name.Trim();
        item.ParentId = request.ParentId;
        item.IsActive = true;
        await SyncTransaction.RunAsync(db, async () =>
        {
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version + 1), false, cancellationToken);
            return item;
        }, cancellationToken);
        return Results.Ok(ToDto(item));
    }

    private static async Task<IResult> UpdateBrandAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = await db.Brands.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        item.Name = request.Name.Trim();
        item.IsActive = true;
        await SyncTransaction.RunAsync(db, async () =>
        {
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version + 1), false, cancellationToken);
            return item;
        }, cancellationToken);
        return Results.Ok(ToDto(item));
    }

    private static async Task<IResult> UpdateProductGroupAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, SaveMasterDataRequest request, CancellationToken cancellationToken)
    {
        var item = await db.ProductGroups.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        item.Name = request.Name.Trim();
        item.IsActive = true;
        await SyncTransaction.RunAsync(db, async () =>
        {
            await EmitAsync(resource, db, businessId, item.Id, ToProjection(item, item.Version + 1), false, cancellationToken);
            return item;
        }, cancellationToken);
        return Results.Ok(ToDto(item));
    }

    private static async Task<IResult> DeleteAsync(ResourceSpec resource, Guid id, ClaimsPrincipal principal, BillingDbContext db, CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        try
        {
            return resource.Kind switch
            {
                "unit" => await DeleteUnitAsync(resource, businessId, id, db, cancellationToken),
                "category" => await DeleteCategoryAsync(resource, businessId, id, db, cancellationToken),
                "brand" => await DeleteBrandAsync(resource, businessId, id, db, cancellationToken),
                "productGroup" => await DeleteProductGroupAsync(resource, businessId, id, db, cancellationToken),
                _ => Results.NotFound(),
            };
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "This master-data row is still referenced by business records.", "Deactivate the row after removing active references, or keep it for historical records.");
        }
    }

    private static async Task<IResult> DeleteUnitAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.Units.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (await db.Products.AnyAsync(product => product.BusinessId == businessId && product.UnitId == id, cancellationToken))
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "The unit is still used by products.", "Move those products to another unit before deleting this unit.");
        }
        db.Units.Remove(item);
        await SyncTransaction.RunAsync(db, async () =>
        {
            await SyncProjections.MasterDataAsync(db, businessId, resource.EntityType, id, null, true, cancellationToken);
            return true;
        }, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteCategoryAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.Categories.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        var referenced = await db.Products.AnyAsync(product => product.BusinessId == businessId && product.CategoryId == id, cancellationToken)
            || await db.Categories.AnyAsync(category => category.BusinessId == businessId && category.ParentId == id, cancellationToken);
        if (referenced)
        {
            item.IsActive = false;
            await SyncTransaction.RunAsync(db, async () =>
            {
                await EmitAsync(resource, db, businessId, id, ToProjection(item, item.Version + 1), false, cancellationToken);
                return true;
            }, cancellationToken);
        }
        else
        {
            db.Categories.Remove(item);
            await SyncTransaction.RunAsync(db, async () =>
            {
                await SyncProjections.MasterDataAsync(db, businessId, resource.EntityType, id, null, true, cancellationToken);
                return true;
            }, cancellationToken);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteBrandAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.Brands.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (await db.Products.AnyAsync(product => product.BusinessId == businessId && product.BrandId == id, cancellationToken))
        {
            item.IsActive = false;
            await SyncTransaction.RunAsync(db, async () =>
            {
                await EmitAsync(resource, db, businessId, id, ToProjection(item, item.Version + 1), false, cancellationToken);
                return true;
            }, cancellationToken);
        }
        else
        {
            db.Brands.Remove(item);
            await SyncTransaction.RunAsync(db, async () =>
            {
                await SyncProjections.MasterDataAsync(db, businessId, resource.EntityType, id, null, true, cancellationToken);
                return true;
            }, cancellationToken);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteProductGroupAsync(ResourceSpec resource, Guid businessId, Guid id, BillingDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.ProductGroups.FirstOrDefaultAsync(row => row.BusinessId == businessId && row.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (await db.Products.AnyAsync(product => product.BusinessId == businessId && product.ProductGroupId == id, cancellationToken))
        {
            item.IsActive = false;
            await SyncTransaction.RunAsync(db, async () =>
            {
                await EmitAsync(resource, db, businessId, id, ToProjection(item, item.Version + 1), false, cancellationToken);
                return true;
            }, cancellationToken);
        }
        else
        {
            db.ProductGroups.Remove(item);
            await SyncTransaction.RunAsync(db, async () =>
            {
                await SyncProjections.MasterDataAsync(db, businessId, resource.EntityType, id, null, true, cancellationToken);
                return true;
            }, cancellationToken);
        }
        return Results.NoContent();
    }

    private static async Task<bool> IsValidCategoryParentAsync(BillingDbContext db, Guid businessId, Guid categoryId, Guid parentId, CancellationToken cancellationToken)
    {
        var ancestors = await db.Categories.AsNoTracking()
            .Where(item => item.BusinessId == businessId && item.IsActive)
            .Select(item => new { item.Id, item.ParentId })
            .ToDictionaryAsync(item => item.Id, item => item.ParentId, cancellationToken);
        if (!ancestors.ContainsKey(parentId)) return false;
        var current = parentId;
        var seen = new HashSet<Guid>();
        while (ancestors.TryGetValue(current, out var next) && next is { } parent)
        {
            if (current == categoryId || !seen.Add(current)) return false;
            current = parent;
        }
        return current != categoryId;
    }

    private static IResult? Validate(ResourceSpec resource, SaveMasterDataRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = request?.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors[nameof(SaveMasterDataRequest.Name)] = [$"Enter a name of 1-{MaxNameLength} characters."];
        }
        if (resource.Kind == "unit")
        {
            var shortName = request?.ShortName?.Trim() ?? string.Empty;
            if (shortName.Length is 0 or > 16)
            {
                errors[nameof(SaveMasterDataRequest.ShortName)] = ["Enter a short unit name of 1-16 characters."];
            }
        }
        return errors.Count == 0 ? null : ApiResults.ValidationProblem("The catalog master-data payload is invalid.", errors);
    }

    private static Task EmitAsync(ResourceSpec resource, BillingDbContext db, Guid businessId, Guid id, object projection, bool tombstone, CancellationToken cancellationToken)
        => SyncProjections.MasterDataAsync(db, businessId, resource.EntityType, id, projection, tombstone, cancellationToken);

    private static object ToProjection(Unit item, long version) => new { id = item.Id, name = item.Name, shortName = item.ShortName, allowDecimals = item.AllowDecimals, version };
    private static object ToProjection(Category item, long version) => new { id = item.Id, name = item.Name, parentId = item.ParentId, isActive = item.IsActive, version };
    private static object ToProjection(Brand item, long version) => new { id = item.Id, name = item.Name, isActive = item.IsActive, version };
    private static object ToProjection(ProductGroup item, long version) => new { id = item.Id, name = item.Name, isActive = item.IsActive, version };
    private static MasterDataDto ToDto(Unit item) => new(item.Id, item.Name, item.ShortName, item.AllowDecimals, null, null, item.Version);
    private static MasterDataDto ToDto(Category item) => new(item.Id, item.Name, null, null, item.ParentId, item.IsActive, item.Version);
    private static MasterDataDto ToDto(Brand item) => new(item.Id, item.Name, null, null, null, item.IsActive, item.Version);
    private static MasterDataDto ToDto(ProductGroup item) => new(item.Id, item.Name, null, null, null, item.IsActive, item.Version);

    private static IResult NotFound() => ApiResults.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "The catalog row does not exist in this business.");
    private static IResult DuplicateName() => ApiResults.Problem(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "A catalog row with the same business key already exists.", "Use a different name or unit short name.");

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static bool IsForeignKeyViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };

    private sealed record ResourceSpec(string Route, string Kind, string EntityType);
}
