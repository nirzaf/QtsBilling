using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Sync;

/// <summary>
/// The sync API of the device: push the local outbox and pull the change feed (QB-22). The device uses
/// the same typed contracts as the browser; it never talks to a database.
/// </summary>
public interface ISyncApiClient
{
    Task<SyncOperationReceipt> PushAsync(SyncOperationEnvelope envelope, CancellationToken cancellationToken = default);

    Task<SyncChangesPage> GetChangesAsync(
        long afterRevision,
        int afterOrdinal,
        int limit = 200,
        CancellationToken cancellationToken = default);
}

/// <summary>HTTP implementation used by the desktop client.</summary>
public sealed class SyncApiClient : ISyncApiClient
{
    public const string RouteBase = "/api/v1/sync";

    private readonly HttpClient _http;

    public SyncApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SyncOperationReceipt> PushAsync(
        SyncOperationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/operations", envelope, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);

        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<SyncOperationReceipt>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The sync endpoint returned an empty receipt.");
    }

    public async Task<SyncChangesPage> GetChangesAsync(
        long afterRevision,
        int afterOrdinal,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var route = $"{RouteBase}/changes?afterRevision={afterRevision}&afterOrdinal={afterOrdinal}&limit={limit}";

        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<SyncChangesPage>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The change feed returned an empty page.");
    }
}
