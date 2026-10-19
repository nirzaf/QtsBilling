namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Product header. Prices, SKUs and stock are deliberately not stored here:
/// stock is derived from <see cref="InventoryBalance"/>, pricing from variants (spec §3.5).
/// </summary>
public sealed class Product : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Sku { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? BrandId { get; set; }

    public Guid? ProductGroupId { get; set; }

    public Guid? UnitId { get; set; }

    public Guid? TaxRateId { get; set; }

    public TaxType TaxType { get; set; } = TaxType.Exclusive;

    public ProductType ProductType { get; set; } = ProductType.Single;

    /// <summary>When false the product is not stock-controlled and stock movements are not created.</summary>
    public bool TrackStock { get; set; } = true;

    public decimal? AlertQuantity { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Sellable variant of a product. A simple product always has exactly one default variant so every
/// transaction line can reference a concrete variant (spec §3.2/§3.5, migration mapping).
/// </summary>
public sealed class ProductVariant : TenantEntity
{
    public Guid ProductId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Sku { get; set; }

    public string? Barcode { get; set; }

    public decimal SellingPrice { get; set; }

    public decimal? PurchasePrice { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
}
