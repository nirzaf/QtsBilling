using System.Net.Http.Json;
using System.Text.Json;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Maui.Services;

public sealed record OidcTokenResponse(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

public sealed class OidcInvalidGrantException : Exception
{
    public OidcInvalidGrantException()
        : base("The sign-in session expired. Sign in again while online.")
    {
    }
}

public interface IOidcTokenClient
{
    Task<OidcTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default);

    Task<OidcTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>Unchallenged transport for OpenIddict's public native-client endpoints.</summary>
public sealed class OidcTokenClient : IOidcTokenClient
{
    private readonly HttpClient _http;

    public OidcTokenClient(HttpClient http) => _http = http;

    public Task<OidcTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
        => PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = "qts-windows",
                ["redirect_uri"] = PkceLoopbackAuthenticator.RedirectUri,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
            },
            cancellationToken);

    public Task<OidcTokenResponse> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
        => PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = "qts-windows",
                ["refresh_token"] = refreshToken,
            },
            cancellationToken);

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(
            "connect/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "qts-windows",
                ["token"] = refreshToken,
                ["token_type_hint"] = "refresh_token",
            }),
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return;
        }

        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OidcTokenResponse> PostTokenAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(
            "connect/token",
            new FormUrlEncodedContent(fields),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error)
                    && string.Equals(error.GetString(), "invalid_grant", StringComparison.Ordinal))
                {
                    throw new OidcInvalidGrantException();
                }
            }
            catch (JsonException)
            {
                // ApiHttp below reports malformed/non-OAuth error payloads without echoing the request.
            }
        }

        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        using var tokenDocument = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = tokenDocument.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? seconds
            : 0;

        if (string.IsNullOrWhiteSpace(accessToken) || expiresIn <= 0)
        {
            throw new ApiException(502, ApiErrorCodes.StoreUnavailable, "The identity server returned an incomplete token response.");
        }

        return new OidcTokenResponse(accessToken, refreshToken, expiresIn);
    }
}
