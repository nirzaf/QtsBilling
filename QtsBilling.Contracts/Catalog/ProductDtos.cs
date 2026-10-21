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
/// Creates a product together with its default variant (a simple product always has exactly one variant, so
/// transaction lines can reference a concrete variant - spec §3.2/§3.5). The brand, group and tax references are
/// optional here but validated against this business when sent, and the unit is chosen because a quantity is
/// meaningless without it. Stock is never a field of this request: a catalogue record cannot state a quantity.
/// </summary>
public sealed record SaveProductRequest(
    string Name,
    string? Sku,
    Guid? UnitId,
    Guid? CategoryId,
    bool TrackStock,
    string? VariantName,
    decimal SellingPrice,
    decimal? PurchasePrice,
    Guid? BrandId = null,
    Guid? ProductGroupId = null,
    Guid? TaxRateId = null,
    string? TaxType = null);

/// <summary>
/// Corrects a product's description and references. The version is the one the caller read, so a stale form is
/// refused instead of overwriting somebody else's change, and activation is deliberately absent: going out of
/// service is its own operation, with a reason (<see cref="ArchiveProductRequest"/>). A unit may only be changed
/// while nothing has moved, because a movement's quantity is already expressed in the old unit.
/// </summary>
public sealed record UpdateProductRequest(
    string Name,
    string? Sku,
    Guid? CategoryId,
    Guid? UnitId,
    Guid? BrandId,
    Guid? ProductGroupId,
    Guid? TaxRateId,
    string? TaxType,
    long ExpectedVersion);

/// <summary>
/// Corrects one variant. <paramref name="ExpectedProductVersion"/> is the product version the caller read: the
/// variant list travels with the product, so one number guards the whole record against a concurrent edit.
/// Active status is not a field here either - see <see cref="ArchiveVariantRequest"/>.
/// </summary>
public sealed record UpdateVariantRequest(
    string Name,
    string? Sku,
    string? Barcode,
    decimal SellingPrice,
    decimal? PurchasePrice,
    long ExpectedProductVersion);

/// <summary>Adds a further variant to an existing product. It is never the default: the default is the variant the product was created with, or one an operator promoted explicitly.</summary>
public sealed record CreateProductVariantRequest(
    string Name,
    string? Sku,
    string? Barcode,
    decimal SellingPrice,
    decimal? PurchasePrice,
    long ExpectedProductVersion);

/// <summary>
/// Takes a product out of service. The reason is mandatory because selection is being withdrawn from the counter,
/// and the version because a catalogue edit must not land over somebody else's. Nothing is deleted: posted lines
/// keep their snapshot and the record stays reachable for history.
/// </summary>
public sealed record ArchiveProductRequest(long ExpectedVersion, string Reason);

/// <summary>Brings a product back. Its variants keep whatever state they were left in, so the response shows how many are sellable.</summary>
public sealed record RestoreProductRequest(long ExpectedVersion, string Reason);

/// <summary>
/// Takes one variant out of service. Archiving the default must name the active sibling that replaces it, because
/// a product with a default nobody can sell is a broken catalogue, and choosing silently would be the server
/// inventing a price (specification task 17, recipe step 1: exactly one active default where required).
/// </summary>
public sealed record ArchiveVariantRequest(long ExpectedProductVersion, string Reason, Guid? NewDefaultVariantId = null);

/// <summary>Shared wire shape for the business-scoped units, categories, brands and product groups.</summary>
public sealed record MasterDataDto(
    Guid Id,
    string Name,
    string? ShortName,
    bool? AllowDecimals,
    Guid? ParentId,
    bool? IsActive,
    long Version);

public sealed record SaveMasterDataRequest(
    string Name,
    string? ShortName = null,
    bool? AllowDecimals = null,
    Guid? ParentId = null);
