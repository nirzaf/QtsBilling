using System.Net.Http.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Contracts.Clients;

/// <summary>
/// Session and account-setup API (QB-04). The web app authenticates with secure cookies; the MAUI client
/// will use the same endpoints through the OIDC/PKCE flow. No credential is ever stored client-side.
/// </summary>
public interface IAccountApiClient
{
    Task<SessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the browser session to another business the same user is an active member of. The server decides
    /// whether the target is allowed and re-issues the membership scope; nothing about an in-flight command or a
    /// device's stored cash changes because a person looked at a different company (specification task 13).
    /// </summary>
    Task<SessionDto> SwitchBusinessAsync(Guid businessId, CancellationToken cancellationToken = default);

    /// <summary>Always succeeds without revealing whether the account exists (202 Accepted).</summary>
    Task<PasswordResetTicketDto?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    Task CompletePasswordResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a one-time local setup/recovery ticket and chooses the account's own password. The ticket decides
    /// whose password is set, so the caller cannot name a target account (specification task 07).
    /// </summary>
    Task CompleteSetupTicketAsync(QtsBilling.Contracts.Administration.CompleteSetupTicketRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountApiClient" />
public sealed class AccountApiClient : IAccountApiClient
{
    public const string RouteBase = "/api/v1/auth";

    private readonly HttpClient _http;

    public AccountApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/login", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<SessionDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"{RouteBase}/session", cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<SessionDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"{RouteBase}/logout", content: null, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionDto> SwitchBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/switch-business", new SwitchBusinessRequest(businessId), ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await ApiHttp.ReadRequiredAsync<SessionDto>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PasswordResetTicketDto?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/password-reset/request", new RequestPasswordResetRequest(email.Trim()), ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            // Production: the ticket is delivered out of band and never returned to the caller.
            return null;
        }

        return await response.Content.ReadFromJsonAsync<PasswordResetTicketDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompletePasswordResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync($"{RouteBase}/password-reset/complete", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSetupTicketAsync(QtsBilling.Contracts.Administration.CompleteSetupTicketRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsJsonAsync("/api/v1/auth/setup-ticket/complete", request, ContractJson.Options, cancellationToken)
            .ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
