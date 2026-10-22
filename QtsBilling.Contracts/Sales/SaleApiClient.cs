using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Sales;

public interface ISaleApiClient
{
    Task<SaleCaptureResult> PostSaleAsync(
        SaleCaptureRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>The server's own receipt for a posted sale: immutable line, payment and change snapshots.</summary>
    Task<SaleReceiptDto> GetReceiptAsync(
        Guid saleId,
        CancellationToken cancellationToken = default);
}

/// <summary>Authenticated HTTP client for the authoritative online POS posting endpoint.</summary>
public sealed class SaleApiClient : ISaleApiClient
{
    public const string Route = "/api/v1/pos/sales/";
    private readonly HttpClient _http;

    public SaleApiClient(HttpClient http) => _http = http;

    public async Task<SaleCaptureResult> PostSaleAsync(
        SaleCaptureRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey), "An idempotency key must be at most 64 characters.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<SaleCaptureResult>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned an empty capture result.");
    }

    public async Task<SaleReceiptDto> GetReceiptAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{Route}{saleId:D}/receipt", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<SaleReceiptDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned an empty receipt.");
    }
}
