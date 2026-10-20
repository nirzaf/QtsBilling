namespace QtsBilling.Contracts.Sales;

using QtsModels.ValueObjects;

/// <summary>
/// Customer cart line captured at sale time. All values are historical snapshots; the server
/// re-validates price/tax/discount policy and never trusts them blindly (specification §3.5/§4.4).
/// </summary>
public sealed record SaleLineRequest(
    Guid VariantId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    string? ObservedCatalogRevision)
{
    /// <summary>
    /// How the tax is composed into the price the client showed. The server is authoritative: it
    /// recomputes the line with the stored catalog policy and rejects a client amount that disagrees.
    /// </summary>
    public CommercialTaxMode? TaxMode { get; init; }

    /// <summary>The pricing policy version the client captured the line against.</summary>
    public string? PricingPolicyVersion { get; init; }
}

/// <summary>
/// Sale capture request shared by the online POS API and the offline sync outbox payload.
/// Offline capture is cash-only; card payments require a verified PSP response (§0.4, §5.3).
/// </summary>
public sealed record SaleCaptureRequest(
    Guid LocationId,
    Guid? CustomerId,
    string Currency,
    IReadOnlyList<SaleLineRequest> Lines,
    PaymentCaptureRequest Payment,
    string? Note,
    Guid? RegisterId);

/// <summary>Cash-only payment capture. Card fields (PAN/CVV) are structurally impossible here.</summary>
public sealed record PaymentCaptureRequest(
    string Method,
    decimal Amount,
    decimal? CashReceived,
    string? Reference,
    TerminalCaptureEnvelope? TerminalCapture = null);

/// <summary>Immutable posted result of a sale capture.</summary>
public sealed record SaleCaptureResult(
    Guid SaleId,
    string? SaleNumber,
    string Currency,
    decimal TotalAmount,
    decimal TaxAmount,
    DateTimeOffset PostedAtUtc,
    bool Provisional);

/// <summary>Wire-visible payment lifecycle state (no false success, specification §3.6).</summary>
public enum PaymentStatus
{
    Pending,
    Confirmed,
    Bounced,
    Refunded,
    Voided,
}

/// <summary>Lifecycle of a posted sale; PostgreSQL sales are authoritative (never provisional).</summary>
public enum SaleState
{
    Posted,
    Reversed,
    NeedsReview,
}

/// <summary>Posted sale returned by the POS read endpoints: an immutable snapshot, never recomputed from the catalog.</summary>
public sealed record PostedSaleDto(
    Guid Id,
    string SaleNumber,
    SaleState Status,
    Guid LocationId,
    Guid? CustomerId,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    PaymentStatus PaymentStatus,
    bool Provisional,
    string? Note,
    DateTimeOffset PostedAtUtc,
    Guid? PostedByUserId,
    Guid? ReversalOfSaleId,
    IReadOnlyList<PostedSaleLineDto> Lines,
    IReadOnlyList<PostedPaymentDto> Payments);

public sealed record PostedSaleLineDto(
    Guid Id,
    int LineNo,
    Guid VariantId,
    string ProductNameSnapshot,
    string? SkuSnapshot,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal LineTotal,
    /// <summary>How the tax was composed into the price (Exclusive, Inclusive or Exempt).</summary>
    string TaxMode,
    decimal UnitCostSnapshot);

public sealed record PostedPaymentDto(
    Guid Id,
    string Method,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    decimal? CashReceived,
    string? Reference,
    string? PspReference,
    DateTimeOffset ReceivedAtUtc,
    string? CardBrand = null,
    string? CardLast4 = null);
