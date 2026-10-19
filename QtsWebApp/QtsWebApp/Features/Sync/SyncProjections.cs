using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Features.Sync;

/// <summary>
/// The projections a device receives through the change feed. They carry the facts the offline screens
/// need and never a secret: no password, no key, no card data.
/// </summary>
public static class SyncProjections
{
    /// <summary>The product with its sellable variants (the price snapshot the device caches).</summary>
    public static Task ProductAsync(
        BillingDbContext db,
        Guid businessId,
        Product product,
        IReadOnlyList<ProductVariant> variants,
        CancellationToken cancellationToken = default)
        => SyncChangeWriter.AppendAsync(
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
            cancellationToken);

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
