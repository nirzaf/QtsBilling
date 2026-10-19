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

    public DeviceAccountApiClient(HttpClient http, ISessionStore sessionStore)
    {
        _inner = new AccountApiClient(http);
        _sessionStore = sessionStore;
    }

    public async Task<SessionDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var session = await GuardAsync(() => _inner.LoginAsync(request, cancellationToken)).ConfigureAwait(false);
        await _sessionStore.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

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
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // Offline: the device keeps working with the last known session instead of failing to start.
            return await _sessionStore.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // The local session is removed even when the server cannot be reached.
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
