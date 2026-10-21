using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;

namespace QtsBilling.Maui.Services;

/// <summary>
/// Device session client. It reuses the shared contract client and only adds the device behaviour:
/// a transport failure never crashes the UI - the last known session is returned instead, so the app
/// starts and stays usable while offline.
/// </summary>
public sealed class DeviceAccountApiClient : IAccountApiClient
{
    private readonly AccountApiClient _inner;
    private readonly ISessionStore _sessionStore;
    private readonly IPkceLoopbackAuthenticator _authenticator;
    private readonly IOidcTokenClient _oidcTokens;

    public DeviceAccountApiClient(
        HttpClient http,
        ISessionStore sessionStore,
        IPkceLoopbackAuthenticator authenticator,
        IOidcTokenClient oidcTokens)
    {
        _inner = new AccountApiClient(http);
        _sessionStore = sessionStore;
        _authenticator = authenticator;
        _oidcTokens = oidcTokens;
    }

    public Task<SessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _authenticator.AuthenticateAsync(cancellationToken);
    }

    /// <summary>
    /// A terminal is enrolled against one business and one branch, and the money it holds belongs to that scope.
    /// Looking at another company is therefore not a device action: it would leave queued cash and stored
    /// operations owned by a business the operator is no longer viewing (specification task 13).
    /// </summary>
    public Task<SessionDto> SwitchBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This terminal is enrolled against one business. Enrol a separate terminal to trade for another company.");

    public async Task<SessionDto?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await _inner.GetSessionAsync(cancellationToken).ConfigureAwait(false);

            if (session is not null)
            {
                await _sessionStore.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }

            return session;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // Offline: the device keeps working with the last known session instead of failing to start.
            return await _sessionStore.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            var cached = await _sessionStore.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            if (exception.StatusCode == 401)
            {
                // A confirmed authentication failure invalidates the secure local session too.
                await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            // The server answered but could not produce a session (it is up but broken). The device keeps
            // the last known session: the server re-checks the enrolment and the permissions for every
            // operation it accepts, so a stale session can never grant more than it did before.
            return cached;
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await _sessionStore.GetTokensAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(tokens?.RefreshToken))
        {
            try
            {
                await _oidcTokens.RevokeRefreshTokenAsync(tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransportFailure(exception) || exception is ApiException)
            {
                // Local sign-out remains available offline; revocation was attempted before secrets are cleared.
            }
        }

        await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<PasswordResetTicketDto?> RequestPasswordResetAsync(
        string email,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => _inner.RequestPasswordResetAsync(email, cancellationToken));

    public Task CompletePasswordResetAsync(
        ResetPasswordRequest request,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => _inner.CompletePasswordResetAsync(request, cancellationToken));

    public Task CompleteSetupTicketAsync(
        QtsBilling.Contracts.Administration.CompleteSetupTicketRequest request,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => _inner.CompleteSetupTicketAsync(request, cancellationToken));

    private static bool IsTransportFailure(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or IOException;

    private static async Task GuardAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw new ApiException(
                503,
                ApiErrorCodes.StoreUnavailable,
                "The server is unreachable.",
                new Dictionary<string, string[]>
                {
                    ["network"] = ["The device is offline or the server cannot be reached. Try again when the connection is restored."],
                });
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw new ApiException(
                503,
                ApiErrorCodes.StoreUnavailable,
                "The server is unreachable.",
                new Dictionary<string, string[]>
                {
                    ["network"] = ["The device is offline or the server cannot be reached. Try again when the connection is restored."],
                });
        }
    }
}
