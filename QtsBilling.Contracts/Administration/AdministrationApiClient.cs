using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Contracts.Sync;

namespace QtsBilling.Contracts.Administration;

/// <summary>
/// The owner-facing setup surface: the business identity, its numbering and its branches. The financial account
/// mappings stay on <c>/api/v1/accounting/settings</c> and the user and terminal screens reuse the administration
/// routes, so no second authority for the same fact is invented here (specification D13).
/// </summary>
public interface IBusinessSetupApiClient
{
    /// <summary>The business identity, its branches, and what still blocks posting.</summary>
    Task<BusinessSettingsDto> GetBusinessSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the identity. A stale <c>ExpectedVersion</c> raises <see cref="ApiException"/> with the conflict code.</summary>
    Task<BusinessSettingsDto> SaveBusinessSettingsAsync(SaveBusinessSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>The branches of the current business, as the setup screen lists them.</summary>
    Task<IReadOnlyList<BusinessLocationDto>> ListLocationsAsync(CancellationToken cancellationToken = default);

    Task<BusinessLocationDto> CreateLocationAsync(CreateLocationRequest request, CancellationToken cancellationToken = default);

    Task<BusinessLocationDto> UpdateLocationAsync(Guid locationId, UpdateLocationRequest request, CancellationToken cancellationToken = default);

    Task<BusinessLocationDto> DeactivateLocationAsync(Guid locationId, DeactivateLocationRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IBusinessSetupApiClient" />
public sealed class BusinessSetupApiClient : IBusinessSetupApiClient
{
    public const string SettingsRoute = "/api/v1/business-settings";
    public const string LocationsRoute = "/api/v1/locations";

    private readonly HttpClient _http;

    public BusinessSetupApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<BusinessSettingsDto> GetBusinessSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(SettingsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessSettingsDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessSettingsDto> SaveBusinessSettingsAsync(SaveBusinessSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync(SettingsRoute, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessSettingsDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BusinessLocationDto>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(LocationsRoute, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<BusinessLocationDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessLocationDto> CreateLocationAsync(CreateLocationRequest request, CancellationToken cancellationToken = default)
        => await PostAsync(LocationsRoute, request, cancellationToken).ConfigureAwait(false);

    public async Task<BusinessLocationDto> UpdateLocationAsync(Guid locationId, UpdateLocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync($"{LocationsRoute}/{locationId}", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessLocationDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BusinessLocationDto> DeactivateLocationAsync(Guid locationId, DeactivateLocationRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{LocationsRoute}/{locationId}/deactivate", request, cancellationToken).ConfigureAwait(false);

    private async Task<BusinessLocationDto> PostAsync(string route, object request, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsJsonAsync(route, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<BusinessLocationDto>(response, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The account surface an owner or manager operates (specification task 14): who belongs to this business, what
/// authority they hold here, and the one-time handover that lets a new person choose their own password. The
/// server is the only authority for the secret - a client never receives it twice and never stores it.
/// </summary>
public interface IAdministrationUserApiClient
{
    /// <summary>One page of this business's accounts, searched by name or email. Never a GUID lookup.</summary>
    Task<AdministrationUserPage> ListUsersAsync(
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the account and its first handover ticket. The secret arrives exactly once.</summary>
    Task<CreateAdministrationUserResultDto> CreateUserAsync(CreateAdministrationUserRequest request, CancellationToken cancellationToken = default);

    Task<AdministrationUserDto> UpdateMembershipAsync(Guid userId, UpdateMembershipRequest request, CancellationToken cancellationToken = default);

    Task<AdministrationUserDto> DisableUserAsync(Guid userId, DeactivateUserRequest request, CancellationToken cancellationToken = default);

    Task<AdministrationUserDto> ActivateUserAsync(Guid userId, ActivateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a fresh setup or recovery ticket after the caller re-authenticated.</summary>
    Task<SetupTicketDto> IssueSetupTicketAsync(Guid userId, IssueSetupTicketRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdministrationUserApiClient" />
public sealed class AdministrationUserApiClient : IAdministrationUserApiClient
{
    public const string RouteBase = "/api/v1/administration/users";

    private readonly HttpClient _http;

    public AdministrationUserApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<AdministrationUserPage> ListUsersAsync(
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        if (pageSize is { } size)
        {
            query.Add($"pageSize={size}");
        }

        var route = query.Count == 0 ? RouteBase : $"{RouteBase}?{string.Join('&', query)}";

        using var response = await _http.GetAsync(route, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<AdministrationUserPage>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateAdministrationUserResultDto> CreateUserAsync(CreateAdministrationUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PostAsJsonAsync(RouteBase, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<CreateAdministrationUserResultDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdministrationUserDto> UpdateMembershipAsync(Guid userId, UpdateMembershipRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PutAsJsonAsync($"{RouteBase}/{userId}/membership", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<AdministrationUserDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdministrationUserDto> DisableUserAsync(Guid userId, DeactivateUserRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{RouteBase}/{userId}/disable", request, cancellationToken).ConfigureAwait(false);

    public async Task<AdministrationUserDto> ActivateUserAsync(Guid userId, ActivateUserRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{RouteBase}/{userId}/activate", request, cancellationToken).ConfigureAwait(false);

    public async Task<SetupTicketDto> IssueSetupTicketAsync(Guid userId, IssueSetupTicketRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/{userId}/setup-ticket", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<SetupTicketDto>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdministrationUserDto> PostAsync(string route, object request, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsJsonAsync(route, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<AdministrationUserDto>(response, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The terminal surface: what each registered device is, where it belongs, what it still holds that has not
/// reached the server, and the lifecycle commands that move it. Reassignment and retirement are refused until a
/// custody resolution exists (specification D07), so the caller sees the server's reason rather than a button
/// that quietly discards money.
/// </summary>
public interface IDeviceAdministrationApiClient
{
    Task<IReadOnlyList<DeviceInfoDto>> ListDevicesAsync(CancellationToken cancellationToken = default);

    Task<DeviceInfoDto> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Opens a short-lived enrolment code for one branch, to be read out to the terminal.</summary>
    Task<CreateEnrolmentChallengeResponse> CreateEnrolmentChallengeAsync(Guid locationId, CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResponse> RevokeAsync(Guid deviceId, RevokeDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records what was reconciled. An acknowledgement may not understate the server's own count.</summary>
    Task<DeviceReassignmentClearanceDto> IssueClearanceAsync(Guid deviceId, IssueReassignmentClearanceRequest request, CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResponse> ReassignAsync(Guid deviceId, ReassignDeviceRequest request, CancellationToken cancellationToken = default);

    Task<DeviceLifecycleResponse> RetireAsync(Guid deviceId, RetireDeviceRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDeviceAdministrationApiClient" />
public sealed class DeviceAdministrationApiClient : IDeviceAdministrationApiClient
{
    public const string RouteBase = "/api/v1/devices";

    private readonly HttpClient _http;

    public DeviceAdministrationApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<DeviceInfoDto>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(RouteBase, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<List<DeviceInfoDto>>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceInfoDto> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/{deviceId}", cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<DeviceInfoDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateEnrolmentChallengeResponse> CreateEnrolmentChallengeAsync(Guid locationId, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/enrolment-challenges", new CreateEnrolmentChallengeRequest(locationId), ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<CreateEnrolmentChallengeResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceLifecycleResponse> RevokeAsync(Guid deviceId, RevokeDeviceRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{RouteBase}/{deviceId}/revoke", request, cancellationToken).ConfigureAwait(false);

    public async Task<DeviceReassignmentClearanceDto> IssueClearanceAsync(Guid deviceId, IssueReassignmentClearanceRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/{deviceId}/reassignment-clearance", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<DeviceReassignmentClearanceDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceLifecycleResponse> ReassignAsync(Guid deviceId, ReassignDeviceRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{RouteBase}/{deviceId}/reassign", request, cancellationToken).ConfigureAwait(false);

    public async Task<DeviceLifecycleResponse> RetireAsync(Guid deviceId, RetireDeviceRequest request, CancellationToken cancellationToken = default)
        => await PostAsync($"{RouteBase}/{deviceId}/retire", request, cancellationToken).ConfigureAwait(false);

    private async Task<DeviceLifecycleResponse> PostAsync(string route, object request, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsJsonAsync(route, request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<DeviceLifecycleResponse>(response, cancellationToken).ConfigureAwait(false);
    }
}
