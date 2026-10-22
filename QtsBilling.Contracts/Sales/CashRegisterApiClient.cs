using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Sales;

/// <summary>
/// Cash register contract. A device is enrolled to one register, so the client needs to know which
/// registers exist and which drawer session is open before it captures a cash sale offline.
/// Every command that changes the drawer carries an idempotency key (specification D06, task 19): the caller
/// keeps the key for as long as the intention lives, so a retry after a lost answer cannot open a second
/// drawer, add a second movement or post a second over/short journal.
/// </summary>
public interface ICashRegisterApiClient
{
    Task<IReadOnlyList<CashRegisterDto>> GetRegistersAsync(CancellationToken cancellationToken = default);

    Task<CashRegisterDto> CreateRegisterAsync(
        CreateCashRegisterRequest request,
        CancellationToken cancellationToken = default);

    Task<CashSessionDetailDto> OpenSessionAsync(
        Guid registerId,
        OpenCashRegisterSessionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>The drawer session this register has open now, with its derived arithmetic.</summary>
    Task<CashSessionDetailDto> GetCurrentSessionAsync(
        Guid registerId,
        CancellationToken cancellationToken = default);

    Task<PagedResult<CashRegisterSessionDto>> ListSessionsAsync(
        Guid registerId,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default);

    Task<CashSessionDetailDto> GetSessionAsync(
        Guid registerId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<CashMovementDto> AddMovementAsync(
        Guid registerId,
        Guid sessionId,
        CashMovementRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>What is still unresolved on this drawer and whether it may be closed at all (specification D07).</summary>
    Task<DrawerCustodyDto> GetCustodyAsync(
        Guid registerId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>The accounts of this business a carry-forward may name as receiving custodian.</summary>
    Task<IReadOnlyList<CashCustodianDto>> GetCustodiansAsync(CancellationToken cancellationToken = default);

    Task<DrawerCustodyDto> CreateCustodyTransferAsync(
        Guid registerId,
        Guid sessionId,
        CreateCustodyTransferRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<CashSessionDetailDto> CloseSessionAsync(
        Guid registerId,
        Guid sessionId,
        CloseCashRegisterSessionRequest request,
        string idempotencyKey,
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

    public async Task<CashRegisterDto> CreateRegisterAsync(
        CreateCashRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(RouteBase, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<CashRegisterDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CashSessionDetailDto> OpenSessionAsync(
        Guid registerId,
        OpenCashRegisterSessionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{RouteBase}/{registerId:D}/sessions")
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<CashSessionDetailDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CashSessionDetailDto> GetCurrentSessionAsync(
        Guid registerId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/{registerId:D}/sessions/current", cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<CashSessionDetailDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<CashRegisterSessionDto>> ListSessionsAsync(
        Guid registerId,
        string? cursor = null,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var query = $"{RouteBase}/{registerId:D}/sessions?pageSize={pageSize}";

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await _http.GetAsync(query, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<PagedResult<CashRegisterSessionDto>>(ContractJson.Options, cancellationToken)
            ?? PagedResult<CashRegisterSessionDto>.Empty(pageSize);
    }

    public async Task<CashSessionDetailDto> GetSessionAsync(
        Guid registerId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/{registerId:D}/sessions/{sessionId:D}", cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<CashSessionDetailDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CashMovementDto> AddMovementAsync(
        Guid registerId,
        Guid sessionId,
        CashMovementRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => await SendWithKeyAsync<CashMovementDto>(
            $"{RouteBase}/{registerId:D}/sessions/{sessionId:D}/movements", request, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

    public async Task<DrawerCustodyDto> GetCustodyAsync(
        Guid registerId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/{registerId:D}/sessions/{sessionId:D}/custody", cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<DrawerCustodyDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CashCustodianDto>> GetCustodiansAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/custodians", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<List<CashCustodianDto>>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public async Task<DrawerCustodyDto> CreateCustodyTransferAsync(
        Guid registerId,
        Guid sessionId,
        CreateCustodyTransferRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => await SendWithKeyAsync<DrawerCustodyDto>(
            $"{RouteBase}/{registerId:D}/sessions/{sessionId:D}/custody-transfers", request, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

    public async Task<CashSessionDetailDto> CloseSessionAsync(
        Guid registerId,
        Guid sessionId,
        CloseCashRegisterSessionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => await SendWithKeyAsync<CashSessionDetailDto>(
            $"{RouteBase}/{registerId:D}/sessions/{sessionId:D}/close", request, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

    private async Task<TResponse> SendWithKeyAsync<TResponse>(
        string route,
        object request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content
            .ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException($"The cash-register API returned an empty {typeof(T).Name}.");
}
