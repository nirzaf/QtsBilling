using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Documents;

/// <summary>
/// The client for local document evidence (specification task 39). Bytes travel only over the authenticated channel
/// the host already uses for every other write: never in a URL, never from a remote fetch, and never as a path a
/// caller chose. The download name is read back from the server's own content disposition and reduced to a bare file
/// name here as well, because a name carrying a separator is a directory.
/// </summary>
public sealed class DocumentApiClient : IDocumentApiClient
{
    public const string EvidenceRoute = "/api/v1/documents/evidence";
    public const string InventoryRoute = "/api/v1/documents/evidence/inventory";
    public const string CleanupRoute = "/api/v1/documents/evidence/orphans/cleanup";

    /// <summary>The largest response this client will hold in memory. The server's own bound is smaller.</summary>
    public const int MaxDownloadBytes = 8_000_000;

    private readonly HttpClient _http;

    public DocumentApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<IReadOnlyList<DocumentEvidenceDto>> ListEvidenceAsync(
        string aggregateType,
        Guid aggregateId,
        bool includeHistory = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);

        var query = $"?aggregateType={Uri.EscapeDataString(aggregateType.Trim())}&aggregateId={aggregateId:D}";

        if (!includeHistory)
        {
            query += "&includeHistory=false";
        }

        using var response = await _http.GetAsync($"{EvidenceRoute}{query}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<List<DocumentEvidenceDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentEvidenceDto> AttachAsync(
        AttachDocumentEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http.PostAsJsonAsync(
            EvidenceRoute, request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<DocumentEvidenceDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentEvidenceDto> ReplaceAsync(
        Guid replacesId,
        ReplaceDocumentEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http.PostAsJsonAsync(
            $"{EvidenceRoute}/{replacesId:D}/replace", request, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<DocumentEvidenceDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentContentDto> DownloadAsync(Guid evidenceId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"{EvidenceRoute}/{evidenceId:D}/content", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            throw new ApiException(
                StatusBadRequest,
                ApiErrorCodes.StoreUnavailable,
                "The evidence arrived with no bytes, which is not a file and will not be presented as one.");
        }

        if (bytes.Length > MaxDownloadBytes)
        {
            throw new ApiException(
                StatusPayloadTooLarge,
                ApiErrorCodes.PayloadTooLarge,
                $"The evidence arrived as {bytes.Length:N0} bytes, past the {MaxDownloadBytes:N0} this host will hold.");
        }

        return new DocumentContentDto(
            SafeFileName(response),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            Sha256Hex(bytes),
            bytes.LongLength,
            bytes);
    }

    public async Task<DocumentEvidenceInventoryDto> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(InventoryRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<DocumentEvidenceInventoryDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentEvidenceCleanupDto> CleanOrphanedEvidenceAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(CleanupRoute, content: null, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<DocumentEvidenceCleanupDto>(response, cancellationToken).ConfigureAwait(false);
    }

    private const int StatusBadRequest = 400;
    private const int StatusPayloadTooLarge = 413;

    /// <summary>
    /// The file name the server composed, reduced to a bare name here as well. A name that still carries a parent
    /// segment or a separator after that reduction is replaced by a fixed one, because the screen must never turn a
    /// response header into a place on the operator's disk.
    /// </summary>
    private static string SafeFileName(HttpResponseMessage response)
    {
        var header = response.Content.Headers.ContentDisposition?.FileName
            ?? ParseFileName(response.Headers.TryGetValues("Content-Disposition", out var values) ? values.FirstOrDefault() : null);

        var candidate = (header ?? string.Empty).Trim().Trim('"');
        var leaf = Path.GetFileName(candidate);

        if (leaf.Contains("..", StringComparison.Ordinal) || leaf.Length == 0 || leaf.Length > 120)
        {
            return "evidence";
        }

        return leaf;
    }

    private static string? ParseFileName(string? disposition)
    {
        if (disposition is null)
        {
            return null;
        }

        foreach (var part in disposition.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                var encoded = part["filename*=".Length..].Trim();
                var quote = encoded.IndexOf('\'');
                var apostrophe = encoded.LastIndexOf('\'');

                if (quote >= 0 && apostrophe > quote)
                {
                    encoded = encoded[(apostrophe + 1)..];
                }

                return encoded;
            }

            if (part.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                return part["filename=".Length..];
            }
        }

        return null;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
