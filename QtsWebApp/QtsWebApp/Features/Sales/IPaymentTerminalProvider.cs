using QtsBilling.Contracts.Sales;

namespace QtsWebApp.Features.Sales;

/// <summary>Adapter boundary for a PSP or physical card terminal.</summary>
public interface IPaymentTerminalProvider
{
    Task<TerminalPaymentIntentDto> CreateIntentAsync(
        Guid businessId,
        Guid locationId,
        decimal amount,
        string currency,
        string idempotencyKey,
        CancellationToken ct);

    Task<TerminalCaptureVerification> VerifyCaptureAsync(
        Guid businessId,
        string pspReference,
        string signatureHeader,
        string rawPayload,
        CancellationToken ct);
}

/// <summary>The production host has no PSP configured, so card processing fails closed by default.</summary>
public sealed class DisabledPaymentTerminalProvider : IPaymentTerminalProvider
{
    public Task<TerminalPaymentIntentDto> CreateIntentAsync(
        Guid businessId,
        Guid locationId,
        decimal amount,
        string currency,
        string idempotencyKey,
        CancellationToken ct)
        => Task.FromException<TerminalPaymentIntentDto>(new PaymentTerminalUnavailableException());

    public Task<TerminalCaptureVerification> VerifyCaptureAsync(
        Guid businessId,
        string pspReference,
        string signatureHeader,
        string rawPayload,
        CancellationToken ct)
        => Task.FromException<TerminalCaptureVerification>(new PaymentTerminalUnavailableException());
}

public sealed class PaymentTerminalUnavailableException : Exception
{
    public PaymentTerminalUnavailableException()
        : base("No verified payment terminal provider is configured.")
    {
    }
}
