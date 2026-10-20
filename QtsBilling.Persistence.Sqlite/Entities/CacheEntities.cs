namespace QtsBilling.Persistence.Sqlite.Entities;

/// <summary>
/// A discardable, revision-tracked copy of the small product projection the POS needs offline.
/// Money is stored as a scaled <c>INTEGER</c> (never <c>REAL</c>); see the SQLite model configuration.
/// </summary>
public sealed class CachedProduct
{
    public Guid BusinessId { get; set; }

    public Guid LocationId { get; set; }

    public Guid ProductId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Catalog unit price, scale 10,000 (four decimal places).</summary>
    public decimal Price { get; set; }

    /// <summary>Tax rate, scale 1,000,000 (six decimal places).</summary>
    public decimal TaxRate { get; set; }

    /// <summary>
    /// How the tax is composed into the price (Exclusive, Inclusive or Exempt). The device needs it to
    /// compute the same tax as the server with the shared calculation (specification D03/F12).
    /// </summary>
    public string TaxMode { get; set; } = "Exclusive";

    public bool Active { get; set; }

    public long Revision { get; set; }
}

/// <summary>Discardable offline customer projection; unknown fields stay in the JSON payload.</summary>
public sealed class CachedCustomer
{
    public Guid BusinessId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>JSON object with only the fields the offline UI needs (no secrets).</summary>
    public string FieldsNeededOffline { get; set; } = "{}";

    public long Revision { get; set; }

    public bool Deleted { get; set; }
}

/// <summary>Discardable business-scoped category projection used by the offline catalog picker.</summary>
public sealed class CachedCategory
{
    public Guid BusinessId { get; set; }

    public Guid CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid? ParentId { get; set; }

    public bool Active { get; set; }

    public long Revision { get; set; }
}

/// <summary>Discardable business-scoped unit-of-measure projection used by the offline catalog.</summary>
public sealed class CachedUnit
{
    public Guid BusinessId { get; set; }

    public Guid UnitId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ShortName { get; set; } = string.Empty;

    public bool AllowDecimals { get; set; }

    public long Revision { get; set; }
}

/// <summary>Discardable authoritative stock projection. The quantity is scaled at 1,000,000.</summary>
public sealed class CachedStock
{
    public Guid BusinessId { get; set; }

    public Guid LocationId { get; set; }

    public Guid VariantId { get; set; }

    public decimal ServerQuantity { get; set; }

    public long Revision { get; set; }
}

/// <summary>Retained only while a down-sync window may still reference a deleted entity.</summary>
public sealed class CacheTombstone
{
    public string EntityType { get; set; } = string.Empty;

    public Guid Id { get; set; }

    public long DeletedRevision { get; set; }
}
