namespace QtsBilling.Contracts.Pos;

using QtsModels.ValueObjects;

/// <summary>
/// A sellable item in the device-local cache. The variant is the unit that is actually sold; the price
/// and tax rate are the snapshot the server will re-validate when the offline sale is pushed.
/// </summary>
public sealed record LocalCatalogItem(
    Guid VariantId,
    string Sku,
    string Name,
    decimal UnitPrice,
    decimal TaxRate,
    decimal? OnHand,
    bool Active,
    long Revision)
{
    /// <summary>
    /// How the tax is composed into the price. The device computes the line with the shared
    /// calculation, so an offline amount and the posted amount agree (specification D03/F12).
    /// </summary>
    public CommercialTaxMode TaxMode { get; init; } = CommercialTaxMode.Exclusive;

    /// <summary>The pricing policy version the cached price was captured under.</summary>
    public string? PricingPolicyVersion { get; init; }
}

/// <summary>One line of a provisional cash sale, with the fixed price snapshot taken at capture time.</summary>
public sealed record LocalSaleLineDraft(
    int Ordinal,
    Guid VariantId,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal Tax,
    decimal Discount,
    long ExpectedCatalogRevision)
{
    /// <summary>How the tax was composed into the captured tax amount.</summary>
    public CommercialTaxMode TaxMode { get; init; } = CommercialTaxMode.Exclusive;

    /// <summary>The pricing policy version the line was captured against.</summary>
    public string? PricingPolicyVersion { get; init; }
}

/// <summary>
/// The cash a cashier collected, with the lines it belongs to. The operation id is generated once per
/// checkout and reused for every retry, so a double click can never create two operations.
/// </summary>
public sealed record LocalCashSaleDraft(
    Guid OperationId,
    decimal CashReceived,
    IReadOnlyList<LocalSaleLineDraft> Lines,
    string? Note = null);

/// <summary>
/// The outcome of a local capture attempt. A capture is only accepted when the money rows are durably
/// committed; a failed transaction never produces an accepted result.
/// </summary>
public sealed record LocalSaleCaptureResult(
    bool Accepted,
    Guid OperationId,
    string? ProvisionalReference,
    decimal Total,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>A locally captured sale that is still waiting for the server.</summary>
public sealed record LocalPendingSale(
    Guid OperationId,
    string ProvisionalReference,
    DateTimeOffset CreatedAtUtc,
    decimal Amount,
    string State,
    string? LastError,
    IReadOnlyList<LocalSaleLineDraft> Lines);

/// <summary>Status of the local queue: what is waiting, what was refused, and how much cash is at risk.</summary>
public sealed record LocalQueueSummary(int Pending, int Rejected, decimal ProvisionalCash);

/// <summary>Stable outcome codes for a local capture (the device never invents server codes).</summary>
public static class LocalCaptureCodes
{
    /// <summary>The operation is durably stored on the device and will be pushed.</summary>
    public const string Captured = "CAPTURED";

    /// <summary>The capture was refused before anything was written.</summary>
    public const string Rejected = "REJECTED";

    /// <summary>This host cannot capture offline sales (the browser client, for example).</summary>
    public const string NotSupported = "NOT_SUPPORTED";
}
