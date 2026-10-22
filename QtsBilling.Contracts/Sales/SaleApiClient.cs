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

    /// <summary>
    /// The server's own posted-sale history, bounded by number, date range, branch and customer
    /// (specification task 21, recipe step 1). A device's provisional capture is never in this list.
    /// </summary>
    Task<PagedResult<SaleHistoryDto>> GetSalesAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? locationId = null,
        Guid? customerId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default);

    /// <summary>The sale with its line, payment and cost snapshots exactly as they were committed.</summary>
    Task<PostedSaleDto> GetSaleAsync(
        Guid saleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the server still allows to be refunded on this sale: remaining quantities and amounts, the original
    /// cost, and the drawers that could pay it (task 21, recipe step 2).
    /// </summary>
    Task<SaleRefundPositionDto> GetRefundPositionAsync(
        Guid saleId,
        CancellationToken cancellationToken = default);

    /// <summary>Every refund already posted against the sale, oldest first - the cumulative evidence.</summary>
    Task<IReadOnlyList<RefundDto>> GetRefundsAsync(
        Guid saleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts one refund. The key is the refund's own stable intention, so a retry answers with the refund that
    /// already exists rather than paying the cash out twice (specification D06).
    /// </summary>
    Task<RefundDto> RefundSaleAsync(
        Guid saleId,
        RefundSaleRequest request,
        string idempotencyKey,
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

    public async Task<PagedResult<SaleHistoryDto>> GetSalesAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? locationId = null,
        Guid? customerId = null,
        string? search = null,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var query = $"?pageSize={Math.Clamp(pageSize, 1, 200)}";

        if (from is { } start)
        {
            query += $"&from={Uri.EscapeDataString(start.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}";
        }

        if (to is { } end)
        {
            query += $"&to={Uri.EscapeDataString(end.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}";
        }

        if (locationId is { } location)
        {
            query += $"&locationId={location:D}";
        }

        if (customerId is { } customer)
        {
            query += $"&customerId={customer:D}";
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += $"&search={Uri.EscapeDataString(search.Trim())}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await _http.GetAsync(Route + query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PagedResult<SaleHistoryDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned an empty history page.");
    }

    public async Task<PostedSaleDto> GetSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{Route}{saleId:D}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PostedSaleDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned an empty sale.");
    }

    public async Task<SaleRefundPositionDto> GetRefundPositionAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{Route}{saleId:D}/refund-position", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<SaleRefundPositionDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned an empty refund position.");
    }

    public async Task<IReadOnlyList<RefundDto>> GetRefundsAsync(
        Guid saleId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{Route}{saleId:D}/refunds", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<List<RefundDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned no refund history.");
    }

    public async Task<RefundDto> RefundSaleAsync(
        Guid saleId,
        RefundSaleRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (idempotencyKey.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey), "An idempotency key must be at most 64 characters.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Route}{saleId:D}/refund")
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<RefundDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The sale API returned an empty refund.");
    }
}
