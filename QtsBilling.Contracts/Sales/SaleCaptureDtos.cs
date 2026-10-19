namespace QtsBilling.Contracts.Sales;

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
    string? ObservedCatalogRevision);

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
    string? Note);

/// <summary>Cash-only payment capture. Card fields (PAN/CVV) are structurally impossible here.</summary>
public sealed record PaymentCaptureRequest(
    string Method,
    decimal Amount,
    decimal? CashReceived,
    string? Reference);

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
    Refunded,
    Voided,
}
