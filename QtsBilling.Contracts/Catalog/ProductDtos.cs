namespace QtsBilling.Contracts.Catalog;

/// <summary>
/// Product catalog entry as exposed to web and the MAUI offline cache.
/// Catalog records never carry stock: authoritative stock lives in inventory balances
/// (specification §3.5, F-12).
/// </summary>
public sealed record ProductDto(
    Guid Id,
    string Name,
    string? Sku,
    Guid? CategoryId,
    Guid? UnitId,
    bool IsActive,
    bool TrackStock,
    long Version);

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
