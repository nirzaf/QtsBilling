using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Audit;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Diagnostics;

/// <summary>
/// One thing an owner has to notice, said as an act rather than a colour: what was seen, what it means, what to do,
/// and the local reference that ties it to the record behind it (specification task 34, recipe step 2).
/// </summary>
public sealed record DiagnosticsAlertDto(
    string Code,
    string Severity,
    string Seen,
    string Sentence,
    string ActToTake,
    string? Reference);

/// <summary>Whether the authoritative store answers, and which server and schema answered it.</summary>
public sealed record StoreDiagnosticsDto(
    bool Reachable,
    long ProbeMilliseconds,
    string DatabaseVersion,
    string SchemaVersion,
    int AppliedMigrations,
    DateTimeOffset ServerTimeUtc);

/// <summary>The volume the instance writes to, measured against the threshold this build warns at.</summary>
public sealed record CapacityDiagnosticsDto(
    string Path,
    long TotalBytes,
    long FreeBytes,
    double UsedPercent,
    int WarnAtPercent,
    bool Warning,
    bool Measured);

/// <summary>The newest backup manifest this instance can find on its own disk, and how old it is.</summary>
public sealed record BackupEvidenceDto(
    bool Configured,
    string? Directory,
    string? FileName,
    DateTimeOffset? CreatedAtUtc,
    long? SizeBytes,
    string? Sha256,
    double? AgeHours,
    int StaleAfterHours,
    bool Stale,
    string? RestoreVerificationNote);

/// <summary>The terminals this business owns, read from their own registration rows.</summary>
public sealed record DeviceFleetDiagnosticsDto(
    int Active,
    int Revoked,
    int Retired,
    int Silent,
    int SilentAfterHours,
    IReadOnlyList<string> SilentDeviceNames,
    int BehindOnSchema,
    int NewestSchemaVersion);

/// <summary>What the change feed holds and how the captured operations were answered.</summary>
public sealed record SyncDiagnosticsDto(
    long CurrentRevision,
    int ChangesInFeed,
    int Accepted,
    int Duplicates,
    int Rejected,
    int RejectedLast24Hours,
    string? NewestRejectionCode,
    DateTimeOffset? NewestRejectionAtUtc,
    string? RejectionReference);

/// <summary>The signing material's own expiry, read from the configured path - or its absence, which is itself a warning.</summary>
public sealed record KeyExpiryDiagnosticsDto(
    bool Configured,
    string? Subject,
    DateTimeOffset? ExpiresAtUtc,
    int? DaysRemaining,
    int WarnBeforeDays,
    bool Warning,
    string Note);

/// <summary>The whole local diagnostic picture, from configured evidence only.</summary>
public sealed record DiagnosticsDto(
    StoreDiagnosticsDto Store,
    CapacityDiagnosticsDto Capacity,
    BackupEvidenceDto Backup,
    DeviceFleetDiagnosticsDto Devices,
    SyncDiagnosticsDto Sync,
    KeyExpiryDiagnosticsDto SigningKey,
    int AuditEventsLast24Hours,
    IReadOnlyList<DiagnosticsAlertDto> Alerts);

/// <summary>
/// What a support bundle is: a file on this machine, named for the request that produced it, listing the sections it
/// carries and the categories it refuses to carry. Nothing leaves the instance unless a person carries the file out.
/// </summary>
public sealed record SupportBundleDto(
    string FileName,
    long Bytes,
    DateTimeOffset GeneratedAtUtc,
    string Reason,
    IReadOnlyList<string> IncludedSections,
    IReadOnlyList<string> ExcludedCategories);

/// <summary>
/// The operator-facing read of the instance's own health: the audit feed, the diagnostics, and a support bundle that
/// is generated locally on request (specification task 34). One client, so the screen and the bundle can never disagree
/// with the routes the server publishes.
/// </summary>
public interface IOperationsApiClient
{
    Task<PagedResult<AuditEventDto>> ListAuditEventsAsync(
        string? entityType = null,
        string? action = null,
        Guid? userId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default);

    Task<DiagnosticsDto> GetDiagnosticsAsync(CancellationToken cancellationToken = default);

    Task<SupportBundleDto> CreateSupportBundleAsync(string reason, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOperationsApiClient" />
public sealed class OperationsApiClient : IOperationsApiClient
{
    public const string AuditRoute = "/api/v1/audit-events";
    public const string DiagnosticsRoute = "/api/v1/diagnostics";
    public const string SupportBundleRoute = "/api/v1/diagnostics/support-bundle";

    private readonly HttpClient _http;

    public OperationsApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<PagedResult<AuditEventDto>> ListAuditEventsAsync(
        string? entityType = null,
        string? action = null,
        Guid? userId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(7);
        if (!string.IsNullOrWhiteSpace(entityType)) query.Add($"entityType={Uri.EscapeDataString(entityType.Trim())}");
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action.Trim())}");
        if (userId is { } actor && actor != Guid.Empty) query.Add($"userId={actor:D}");
        if (from is { } start) query.Add($"from={start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
        if (to is { } end) query.Add($"to={end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor.Trim())}");
        if (pageSize is { } size) query.Add($"pageSize={size.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        var route = query.Count == 0 ? AuditRoute : $"{AuditRoute}?{string.Join('&', query)}";
        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<PagedResult<AuditEventDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticsDto> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(DiagnosticsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<DiagnosticsDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupportBundleDto> CreateSupportBundleAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var response = await _http.PostAsJsonAsync(
            SupportBundleRoute,
            new CreateSupportBundleRequest(reason.Trim()),
            ContractJson.Options,
            cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ApiHttp.ReadRequiredAsync<SupportBundleDto>(response, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The reason an owner asked for a bundle. It is recorded in the bundle, because an unexplained export is not auditable.</summary>
public sealed record CreateSupportBundleRequest(string Reason);
