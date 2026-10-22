using QtsBilling.Contracts.Catalog;

namespace QtsBilling.Ui.Models;

/// <summary>
/// One row of the catalog list, with the references the server identifies by number resolved to the names an
/// operator recognises (specification requirement R07). Nothing here is editable: the row is a way to find the
/// record whose editor will change it.
/// </summary>
public sealed class ProductListItem
{
    private readonly Func<Guid?, string?> _nameOf;

    public ProductListItem(ProductDto product, Func<Guid?, string?> nameOf)
    {
        Product = product;
        _nameOf = nameOf;
    }

    public ProductDto Product { get; }

    public Guid Id => Product.Id;

    public string Name => Product.Name;

    public string? Sku => Product.Sku;

    public string UnitName => _nameOf(Product.UnitId) ?? "no unit - a quantity would be meaningless";

    public string CategoryName => _nameOf(Product.CategoryId) ?? "uncategorised";

    public bool TrackStock => Product.TrackStock;

    public bool IsActive => Product.IsActive;

    public long Version => Product.Version;

    public string StateText => IsActive ? "Active" : "Out of service";

    public string KindText => TrackStock ? "Stock" : "Service";

    /// <summary>The price line a list row shows: the tax mode reads with it, because 100.00 means two different things.</summary>
    public string TaxText => $"{Pages.Catalog.CatalogRules.Percent(Product.TaxRate)} {Pages.Catalog.CatalogRules.TaxModeText(Product.TaxType)}";
}

/// <summary>
/// What the operator has typed about a product, held apart from the server's record so a refusal cannot erase it
/// (specification task 18, T18.02). The identifiers arrive from the name pickers; a human never types one.
/// </summary>
public sealed class ProductForm
{
    public string Name { get; set; } = string.Empty;

    public string? Sku { get; set; }

    public Guid? UnitId { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? BrandId { get; set; }

    public Guid? ProductGroupId { get; set; }

    public Guid? TaxRateId { get; set; }

    public string TaxType { get; set; } = Pages.Catalog.CatalogRules.TaxExclusive;

    public bool TrackStock { get; set; } = true;

    /// <summary>Set once the record exists: creation cannot be redone by an edit, and an edit needs the version it saw.</summary>
    public Guid? ProductId { get; set; }

    public long Version { get; set; }

    /// <summary>True while the operator is describing something that does not exist yet.</summary>
    public bool IsNew { get; set; } = true;

    /// <summary>The last refusal, kept with the typed values so the next attempt is a correction, not a retyping.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// What the screen can accept: a name, and a unit for anything new, because a quantity printed on a document
    /// is meaningless without one (the server refuses a name and accepts an absent unit; this is the stricter
    /// default an operator is asked for).
    /// </summary>
    public bool CanSave => Name.Trim().Length > 0 && (!IsNew || UnitId is not null);

    public void Load(ProductDto product)
    {
        Name = product.Name;
        Sku = product.Sku;
        UnitId = product.UnitId;
        CategoryId = product.CategoryId;
        BrandId = product.BrandId;
        ProductGroupId = product.ProductGroupId;
        TaxRateId = product.TaxRateId;
        TaxType = product.TaxType;
        TrackStock = product.TrackStock;
        ProductId = product.Id;
        Version = product.Version;
        IsNew = false;
        Note = null;
    }

    public SaveProductRequest ToCreate(string? variantName, decimal sellingPrice, decimal? purchasePrice)
        => new(
            Name.Trim(),
            string.IsNullOrWhiteSpace(Sku) ? null : Sku.Trim(),
            UnitId,
            CategoryId,
            TrackStock,
            string.IsNullOrWhiteSpace(variantName) ? Name.Trim() : variantName.Trim(),
            sellingPrice,
            purchasePrice,
            BrandId,
            ProductGroupId,
            TaxRateId,
            TaxType);

    public UpdateProductRequest ToUpdate()
        => new(
            Name.Trim(),
            string.IsNullOrWhiteSpace(Sku) ? null : Sku.Trim(),
            CategoryId,
            UnitId,
            BrandId,
            ProductGroupId,
            TaxRateId,
            TaxType,
            Version);

    /// <summary>Records a refusal against the values still on screen; nothing here reloads or clears them.</summary>
    public void HoldNote(string note) => Note = note;
}

/// <summary>
/// One variant as the editor holds it. <see cref="VariantId"/> is null while a new one is being described, and
/// <see cref="ExpectedProductVersion"/> is always the product version the screen read, because the variant list
/// travels with the product (specification task 17).
/// </summary>
public sealed class VariantForm
{
    public Guid? VariantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Sku { get; set; }

    public string? Barcode { get; set; }

    public decimal SellingPrice { get; set; }

    public decimal? PurchasePrice { get; set; }

    public long ExpectedProductVersion { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public long Version { get; set; }

    public string? Note { get; set; }

    public bool CanSave => Name.Trim().Length > 0;

    public void Load(ProductVariantDto variant, long productVersion)
    {
        VariantId = variant.Id;
        Name = variant.Name;
        Sku = variant.Sku;
        Barcode = variant.Barcode;
        SellingPrice = variant.SellingPrice;
        PurchasePrice = variant.PurchasePrice;
        ExpectedProductVersion = productVersion;
        IsDefault = variant.IsDefault;
        IsActive = variant.IsActive;
        Version = variant.Version;
        Note = null;
    }

    public void ClearForNew(long productVersion)
    {
        VariantId = null;
        Name = string.Empty;
        Sku = null;
        Barcode = null;
        SellingPrice = 0m;
        PurchasePrice = null;
        ExpectedProductVersion = productVersion;
        IsDefault = false;
        IsActive = true;
        Version = 0;
        Note = null;
    }

    public CreateProductVariantRequest ToCreate()
        => new(
            Name.Trim(),
            string.IsNullOrWhiteSpace(Sku) ? null : Sku.Trim(),
            string.IsNullOrWhiteSpace(Barcode) ? null : Barcode.Trim(),
            SellingPrice,
            PurchasePrice,
            ExpectedProductVersion);

    public UpdateVariantRequest ToUpdate()
        => new(
            Name.Trim(),
            string.IsNullOrWhiteSpace(Sku) ? null : Sku.Trim(),
            string.IsNullOrWhiteSpace(Barcode) ? null : Barcode.Trim(),
            SellingPrice,
            PurchasePrice,
            ExpectedProductVersion);

    public void HoldNote(string note) => Note = note;
}

/// <summary>
/// A variant row plus the buffer an open editor holds. The pair is what makes "no lost input" testable: the
/// server's record and the operator's unsaved correction are visibly different things (task 18, T18.02).
/// </summary>
public sealed class VariantRow(ProductVariantDto variant)
{
    public ProductVariantDto Variant { get; private set; } = variant;

    public VariantForm Buffer { get; } = new();

    public bool IsEditing { get; set; }

    /// <summary>
    /// Quantity on the shelf across the branches the screen read. Stock is never written from the catalog: this is
    /// the inventory ledger's answer, shown so an archive can disclose what is still held (task 18, step 3).
    /// </summary>
    public decimal StockTotal { get; set; }

    public Guid Id => Variant.Id;

    public string Name => Variant.Name;

    public string? Sku => Variant.Sku;

    public string? Barcode => Variant.Barcode;

    public bool IsActive => Variant.IsActive;

    public bool IsDefault => Variant.IsDefault;

    public void BeginEdit(long productVersion)
    {
        Buffer.Load(Variant, productVersion);
        IsEditing = true;
    }

    public void Apply(ProductVariantDto updated)
    {
        Variant = updated;
        IsEditing = false;
        Buffer.Note = null;
    }

    public void Cancel()
    {
        Buffer.Note = null;
        IsEditing = false;
    }
}
