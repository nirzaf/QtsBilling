namespace QtsBilling.Contracts.Catalog;

/// <summary>
/// Product catalog entry as exposed to web and the MAUI offline cache.
/// Catalog records never carry stock: authoritative stock lives in inventory balances
/// (specification §3.5, F-12). The tax rate and tax type travel with the product so a device can
/// cache the exact sellable price snapshot the server will re-validate when an offline sale is pushed.
/// </summary>
public sealed record ProductDto(
    Guid Id,
    string Name,
    string? Sku,
    Guid? CategoryId,
    Guid? UnitId,
    bool IsActive,
    bool TrackStock,
    long Version,
    decimal TaxRate = 0m,
    string TaxType = "Exclusive");

/// <summary>
/// Price and tax snapshot for a variant at a known catalog revision. Offline sales record the
/// observed revision so the server can apply the approved price-tolerance policy (§4.6).
/// </summary>
public sealed record VariantPriceSnapshotDto(
    Guid VariantId,
    Guid ProductId,
    string Name,
    string Currency,
    decimal UnitPrice,
    decimal TaxRate,
    string? CatalogRevision,
    long Version);

/// <summary>Authoritative stock snapshot projected for one location.</summary>
public sealed record StockSnapshotDto(
    Guid VariantId,
    Guid ProductId,
    Guid LocationId,
    decimal OnHand,
    long Revision);

public sealed record ProductVariantDto(
    Guid Id,
    Guid ProductId,
    string Name,
    string? Sku,
    string? Barcode,
    decimal SellingPrice,
    decimal? PurchasePrice,
    bool IsDefault,
    bool IsActive,
    long Version);

public sealed record ProductDetailDto(ProductDto Product, IReadOnlyList<ProductVariantDto> Variants);

/// <summary>
/// Creates a product together with its default variant (a simple product always has exactly one variant,
/// so transaction lines can reference a concrete variant — spec §3.2/§3.5).
/// </summary>
public sealed record SaveProductRequest(
    string Name,
    string? Sku,
    Guid? UnitId,
    Guid? CategoryId,
    bool TrackStock,
    string? VariantName,
    decimal SellingPrice,
    decimal? PurchasePrice);

public sealed record UpdateProductRequest(string Name, string? Sku, Guid? CategoryId, bool IsActive);

public sealed record UpdateVariantRequest(string Name, string? Sku, string? Barcode, decimal SellingPrice, decimal? PurchasePrice, bool IsActive);
