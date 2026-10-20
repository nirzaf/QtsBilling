using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Serialization;

namespace QtsBilling.Maui.Services;

public interface IPkceLoopbackAuthenticator
{
    Task<SessionDto> AuthenticateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Completes the public Windows client's authorization-code flow in the system browser.</summary>
public sealed class PkceLoopbackAuthenticator : IPkceLoopbackAuthenticator
{
    public const string RedirectUri = "http://127.0.0.1:5199/callback";
    private const string ClientId = "qts-windows";
    private const string Scope = "openid offline_access qts.api";
    private const string ListenerPrefix = "http://127.0.0.1:5199/";

    private readonly HttpClient _http;
    private readonly IOidcTokenClient _tokens;
    private readonly ISessionStore _sessionStore;
    private readonly IServerSettings _settings;

    public PkceLoopbackAuthenticator(
        HttpClient http,
        IOidcTokenClient tokens,
        ISessionStore sessionStore,
        IServerSettings settings)
    {
        _http = http;
        _tokens = tokens;
        _sessionStore = sessionStore;
        _settings = settings;
    }

    public async Task<SessionDto> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenerPrefix);
        listener.Start();

        try
        {
            var authorizeUrl = await BuildAuthorizeUrlAsync(challenge, state, cancellationToken).ConfigureAwait(false);
            await Launcher.Default.OpenAsync(new Uri(authorizeUrl)).ConfigureAwait(false);

            var code = await ReceiveAuthorizationCodeAsync(listener, state, cancellationToken).ConfigureAwait(false);
            var tokenResponse = await _tokens
                .ExchangeAuthorizationCodeAsync(code, verifier, cancellationToken)
                .ConfigureAwait(false);
            var session = await GetSessionAsync(tokenResponse.AccessToken, cancellationToken).ConfigureAwait(false);

            if (session.BusinessId is not { } businessId)
            {
                throw new ApiException(403, ApiErrorCodes.Forbidden, "This account has no active business membership.");
            }

            await _sessionStore.SaveTokensAsync(
                new StoredOidcTokens(
                    tokenResponse.AccessToken,
                    tokenResponse.RefreshToken,
                    DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresInSeconds),
                    session.UserId,
                    businessId),
                cancellationToken).ConfigureAwait(false);
            await _sessionStore.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<string> BuildAuthorizeUrlAsync(
        string challenge,
        string state,
        CancellationToken cancellationToken)
    {
        var query = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        });
        var encoded = await query.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var baseUri = _settings.ServerBaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return $"{baseUri}/connect/authorize?{encoded}";
    }

    private static async Task<string> ReceiveAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            using (context.Response)
            {
                if (context.Request.RemoteEndPoint is not { } remoteEndPoint
                    || !IPAddress.IsLoopback(remoteEndPoint.Address)
                    || !string.Equals(context.Request.Url?.AbsolutePath, "/callback", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await context.Response.OutputStream.WriteAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var actualState = context.Request.QueryString["state"];
                if (actualState is null || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(expectedState),
                        Encoding.ASCII.GetBytes(actualState)))
                {
                    await WriteCompletionPageAsync(context.Response, success: false, cancellationToken).ConfigureAwait(false);
                    throw new ApiException(400, ApiErrorCodes.Unauthenticated,
                        "The sign-in response could not be verified. Close the browser and try again.");
                }

                var error = context.Request.QueryString["error"];
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await WriteCompletionPageAsync(context.Response, success: false, cancellationToken).ConfigureAwait(false);
                    var description = context.Request.QueryString["error_description"];
                    throw new ApiException(400, ApiErrorCodes.Unauthenticated,
                        string.IsNullOrWhiteSpace(description) ? "The identity server declined sign-in." : description);
                }

                var code = context.Request.QueryString["code"];
                if (string.IsNullOrWhiteSpace(code))
                {
                    await WriteCompletionPageAsync(context.Response, success: false, cancellationToken).ConfigureAwait(false);
                    throw new ApiException(400, ApiErrorCodes.Unauthenticated,
                        "The identity server did not return an authorization code.");
                }

                await WriteCompletionPageAsync(context.Response, success: true, cancellationToken).ConfigureAwait(false);
                return code;
            }
        }
    }

    private async Task<SessionDto> GetSessionAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_settings.ServerBaseUrl, "/api/v1/auth/session"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ApiHttp.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<SessionDto>(ContractJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApiException(502, ApiErrorCodes.StoreUnavailable, "The identity server returned no session.");
    }

    private static async Task WriteCompletionPageAsync(
        HttpListenerResponse response,
        bool success,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        var message = success
            ? "Sign-in is complete. You can close this browser window and return to QTS Billing."
            : "Sign-in could not be verified. Close this browser window and return to QTS Billing.";
        var content = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>QTS Billing</title></head><body><p>{message}</p></body></html>");
        response.ContentLength64 = content.Length;
        await response.OutputStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
