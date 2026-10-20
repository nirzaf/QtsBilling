using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Features.Sync;

/// <summary>
/// The projections a device receives through the change feed. They carry the facts the offline screens
/// need and never a secret: no password, no key, no card data.
/// </summary>
public static class SyncProjections
{
    /// <summary>Projection for a catalog master-data row not otherwise represented by a device command.</summary>
    public static Task MasterDataAsync(
        BillingDbContext db,
        Guid businessId,
        string resourceType,
        Guid recordId,
        object? projection,
        bool tombstone,
        CancellationToken cancellationToken = default)
        => SyncChangeWriter.AppendAsync(
            db,
            businessId,
            resourceType,
            recordId,
            tombstone ? SyncChangeKind.Tombstone : SyncChangeKind.Upsert,
            tombstone ? null : SyncChangeWriter.Projection(projection),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>The product with its sellable variants (the price snapshot the device caches).</summary>
    public static async Task ProductAsync(
        BillingDbContext db,
        Guid businessId,
        Product product,
        IReadOnlyList<ProductVariant> variants,
        CancellationToken cancellationToken = default)
    {
        // The device needs the tax rate itself (not only its id) to cache a sellable price snapshot.
        var taxRate = product.TaxRateId is { } taxRateId
            ? await db.TaxRates.AsNoTracking()
                .Where(rate => rate.Id == taxRateId)
                .Select(rate => rate.Rate)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : 0m;

        await SyncChangeWriter.AppendAsync(
            db,
            businessId,
            "product",
            product.Id,
            SyncChangeKind.Upsert,
            SyncChangeWriter.Projection(new
            {
                id = product.Id,
                name = product.Name,
                sku = product.Sku,
                isActive = product.IsActive,
                trackStock = product.TrackStock,
                taxRateId = product.TaxRateId,
                taxRate,
                taxType = product.TaxType.ToString(),
                version = product.Version,
                variants = variants
                    .Select(variant => new
                    {
                        id = variant.Id,
                        name = variant.Name,
                        sku = variant.Sku,
                        sellingPrice = variant.SellingPrice,
                        isActive = variant.IsActive,
                        isDefault = variant.IsDefault,
                        version = variant.Version,
                    })
                    .ToArray(),
            }),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The customer projection. A deactivation is a tombstone, so a device drops the record.</summary>
    public static Task CustomerAsync(
        BillingDbContext db,
        Guid businessId,
        Customer customer,
        bool tombstone,
        CancellationToken cancellationToken = default)
        => SyncChangeWriter.AppendAsync(
            db,
            businessId,
            "customer",
            customer.Id,
            tombstone ? SyncChangeKind.Tombstone : SyncChangeKind.Upsert,
            tombstone
                ? null
                : SyncChangeWriter.Projection(new
                {
                    id = customer.Id,
                    name = customer.Name,
                    email = customer.Email,
                    phone = customer.Phone,
                    address = customer.Address,
                    isActive = customer.IsActive,
                    version = customer.Version,
                }),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>The stock projection for one variant at one location.</summary>
    public static Task StockAsync(
        BillingDbContext db,
        Guid businessId,
        Guid locationId,
        Guid variantId,
        decimal onHand,
        CancellationToken cancellationToken = default)
        => SyncChangeWriter.AppendAsync(
            db,
            businessId,
            "stock",
            variantId,
            SyncChangeKind.Upsert,
            SyncChangeWriter.Projection(new
            {
                variantId,
                locationId,
                onHand,
            }),
            DateTimeOffset.UtcNow,
            cancellationToken);
}
