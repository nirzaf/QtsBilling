namespace QtsBilling.Contracts.Sales;

/// <summary>Request for a provider-owned terminal intent; card data is entered only at the provider.</summary>
public sealed record CreateTerminalPaymentIntentRequest(Guid LocationId, decimal Amount, string Currency);

/// <summary>Provider-issued intent data safe for the POS client to use with its configured terminal.</summary>
public sealed record TerminalPaymentIntentDto(
    string PspReference,
    Guid LocationId,
    decimal Amount,
    string Currency,
    string Status,
    string? NextActionUrl,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Signed PSP capture payload passed through to the provider for verification. The server does not
/// persist or log this payload; callers must never place card numbers or security codes in it.
/// </summary>
public sealed record TerminalCaptureEnvelope(string SignatureHeader, string RawPayload);

/// <summary>Provider's verified capture facts. Only non-sensitive card brand and final four are retained.</summary>
public sealed record TerminalCaptureVerification(
    bool Verified,
    string PspReference,
    Guid LocationId,
    decimal Amount,
    string Currency,
    string? CardBrand,
    string? CardLast4);
