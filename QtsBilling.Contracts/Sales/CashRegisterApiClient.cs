using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Sales;

/// <summary>
/// Cash register contract. A device is enrolled to one register, so the client needs to know which
/// registers exist and which drawer session is open before it captures a cash sale offline.
/// </summary>
public interface ICashRegisterApiClient
{
    Task<IReadOnlyList<CashRegisterDto>> GetRegistersAsync(CancellationToken cancellationToken = default);

    Task<CashSessionDetailDto> OpenSessionAsync(
        Guid registerId,
        OpenCashRegisterSessionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation shared by the browser client, server prerender and MAUI.</summary>
public sealed class CashRegisterApiClient : ICashRegisterApiClient
{
    public const string RouteBase = "/api/v1/pos/registers";

    private readonly HttpClient _http;

    public CashRegisterApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<CashRegisterDto>> GetRegistersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(RouteBase, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<List<CashRegisterDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public async Task<CashSessionDetailDto> OpenSessionAsync(
        Guid registerId,
        OpenCashRegisterSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"{RouteBase}/{registerId:D}/sessions",
            request,
            ContractJson.Options,
            cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<CashSessionDetailDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The cash-register API returned an empty session result.");
    }
}
