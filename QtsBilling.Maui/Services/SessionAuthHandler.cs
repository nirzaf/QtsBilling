using System.Net;
using System.Net.Http.Headers;
using QtsBilling.Maui.Services;

namespace QtsBilling.Maui.Services;

/// <summary>Attaches a securely stored OpenID Connect access token and rotates it before expiry.</summary>
public sealed class SessionAuthHandler : DelegatingHandler
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(2);
    private readonly ISessionStore _sessionStore;
    private readonly IOidcTokenClient _tokenClient;
    private readonly IDeviceApiLog _apiLog;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public SessionAuthHandler(ISessionStore sessionStore, IOidcTokenClient tokenClient, IDeviceApiLog apiLog)
    {
        _sessionStore = sessionStore;
        _tokenClient = tokenClient;
        _apiLog = apiLog;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var route = request.RequestUri is { } uri ? uri.PathAndQuery : "<unknown>";
        await _apiLog.WriteAsync($"-> {request.Method} {route}", cancellationToken).ConfigureAwait(false);

        try
        {
            var (tokens, refreshedBeforeSend) = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            if (tokens is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (refreshedBeforeSend)
                {
                    await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (tokens?.RefreshToken is { Length: > 0 })
                {
                    // Do not replay a potentially mutating request. Rotate once for the next API call;
                    // an invalid_grant or a second 401 forces a fresh browser sign-in.
                    var refreshed = await RefreshAfterUnauthorizedAsync(tokens, cancellationToken).ConfigureAwait(false);
                    if (refreshed.InvalidGrant)
                    {
                        await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await _apiLog.WriteAsync(
                $"<- {request.Method} {route} status={(int)response.StatusCode} elapsedMs={started.ElapsedMilliseconds}",
                cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _apiLog.WriteAsync(
                $"!! {request.Method} {route} failed={exception.GetType().Name} elapsedMs={started.ElapsedMilliseconds}",
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(StoredOidcTokens? Tokens, bool Refreshed)> RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        var tokens = await _sessionStore.GetTokensAsync(cancellationToken).ConfigureAwait(false);
        if (tokens is null || tokens.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshWindow))
        {
            return (tokens, false);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _sessionStore.GetTokensAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return (null, false);
            }

            if (current.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshWindow))
            {
                return (current, false);
            }

            var refreshed = await RefreshCoreAsync(current, cancellationToken).ConfigureAwait(false);
            return (refreshed.Tokens, refreshed.Refreshed);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<(StoredOidcTokens? Tokens, bool InvalidGrant)> RefreshAfterUnauthorizedAsync(
        StoredOidcTokens rejectedTokens,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _sessionStore.GetTokensAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return (null, true);
            }

            if (!string.Equals(current.AccessToken, rejectedTokens.AccessToken, StringComparison.Ordinal))
            {
                return (current, false);
            }

            var refreshed = await RefreshCoreAsync(current, cancellationToken).ConfigureAwait(false);
            return (refreshed.Tokens, refreshed.InvalidGrant);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<(StoredOidcTokens? Tokens, bool Refreshed, bool InvalidGrant)> RefreshCoreAsync(
        StoredOidcTokens current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            return (null, false, true);
        }

        try
        {
            var result = await _tokenClient.RefreshAsync(current.RefreshToken, cancellationToken).ConfigureAwait(false);
            var updated = current with
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken ?? current.RefreshToken,
                AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresInSeconds),
            };
            await _sessionStore.SaveTokensAsync(updated, cancellationToken).ConfigureAwait(false);
            return (updated, true, false);
        }
        catch (OidcInvalidGrantException)
        {
            await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            return (null, false, true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // A network outage must not erase an offline grant or a refresh token that may still work.
            return (current, false, false);
        }
    }
}
