using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Sales;

public interface IPaymentTerminalApiClient
{
    Task<TerminalPaymentIntentDto> CreateIntentAsync(
        CreateTerminalPaymentIntentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Authenticated typed client for server-created PSP or physical-terminal intents.</summary>
public sealed class PaymentTerminalApiClient : IPaymentTerminalApiClient
{
    public const string Route = "/api/v1/pos/card-intents";
    private readonly HttpClient _http;

    public PaymentTerminalApiClient(HttpClient http) => _http = http;

    public async Task<TerminalPaymentIntentDto> CreateIntentAsync(
        CreateTerminalPaymentIntentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey), "An idempotency key must be at most 64 characters.");

        using var message = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<TerminalPaymentIntentDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The payment terminal API returned an empty intent.");
    }
}
