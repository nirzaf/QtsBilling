using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The native identity endpoint (specification task 22, finding F07/F10): the Windows public client
/// obtains an authorization code through a browser session and exchanges it with a PKCE verifier.
/// </summary>
/// <remarks>
/// Every test drives the real host against the real PostgreSQL. The client is public: no secret is
/// sent, and possession of the code is proven by the verifier. The access token carries the identity and
/// the business, never the permissions; the last test proves that revoking the membership removes the
/// authority while the token is still valid.
/// </remarks>
public sealed class NativeAuthorizationTests : IClassFixture<PostgresApiFixture>
{
    private const string RedirectUri = "http://127.0.0.1:5199/callback";
    private const string ClientId = "qts-windows";
    private const string Scope = "openid offline_access qts.api";
    private const string Password = "Native!Passw0rd2026";

    private readonly PostgresApiFixture _fixture;

    public NativeAuthorizationTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T22_01_discovery_and_the_authorization_code_flow_with_pkce()
    {
        var discovery = await _fixture.CreateAnonymousClient().GetAsync("/.well-known/openid-configuration");
        Assert.True(discovery.IsSuccessStatusCode, await discovery.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        var configuration = document.RootElement;

        Assert.EndsWith("/connect/authorize", configuration.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/connect/token", configuration.GetProperty("token_endpoint").GetString());

        var methods = configuration.GetProperty("code_challenge_methods_supported")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("S256", methods);

        var session = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var verifier = CreateVerifier();
        var code = await AuthorizeAsync(session, verifier);

        var tokens = await ExchangeAsync(session, code, verifier);
        Assert.False(string.IsNullOrWhiteSpace(tokens.GetProperty("access_token").GetString()));
        Assert.True(tokens.TryGetProperty("refresh_token", out var refreshToken));

        // The bearer client shares no cookie with the browser session: the access token alone is the
        // authority, and the business scope it carries is the membership the user signed in with.
        var bearer = _fixture.CreateAnonymousClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        var customers = await bearer.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.OK, customers.StatusCode);

        // A refresh token is single-use: the exchange succeeds once and issues a new pair.
        var refreshed = await RefreshAsync(session, refreshToken.GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.GetProperty("access_token").GetString()));

        // Replaying the consumed refresh token is refused.
        var replay = await RefreshRawAsync(session, refreshToken.GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task T22_02_a_wrong_verifier_and_a_replayed_code_are_refused()
    {
        var session = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        // The code is bound to the challenge: another verifier cannot redeem it.
        var verifier = CreateVerifier();
        var code = await AuthorizeAsync(session, verifier);
        var wrongVerifier = await ExchangeRawAsync(session, code, CreateVerifier());
        Assert.Equal(HttpStatusCode.BadRequest, wrongVerifier.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(wrongVerifier));

        // The right verifier redeems the code once; the same code cannot be presented again, so a code
        // captured from the browser history is worthless after the first exchange.
        var secondVerifier = CreateVerifier();
        var secondCode = await AuthorizeAsync(session, secondVerifier);
        var accepted = await ExchangeRawAsync(session, secondCode, secondVerifier);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var replay = await ExchangeRawAsync(session, secondCode, secondVerifier);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task T22_03_the_authorization_endpoint_requires_the_signed_in_membership()
    {
        // No cookie: the native app never receives a token without a human signing in.
        var verifier = CreateVerifier();
        var response = await AuthorizeRawAsync(_fixture.CreateAnonymousClient(), verifier);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());

        // An address that is not registered for the client is refused even though the session is valid.
        var session = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var hostile = await session.PostAsync("/connect/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = "https://attacker.example/callback",
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["state"] = Guid.NewGuid().ToString("N"),
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, hostile.StatusCode);
    }

    [Fact]
    public async Task T22_04_the_access_token_loses_authority_when_the_membership_is_revoked()
    {
        var email = $"native-{Guid.NewGuid():N}@qtsbilling.local";
        Guid userId;

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            var user = new User
            {
                UserName = email,
                Email = email,
                DisplayName = "Native authorization test",
                EmailConfirmed = true,
                IsActive = true,
                PasswordResetRequired = false,
            };

            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                UserId = user.Id,
                Role = MembershipRole.Owner,
                IsActive = true,
            });

            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var session = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await session.PostAsJsonAsync("/api/v1/auth/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
        });
        login.EnsureSuccessStatusCode();

        var verifier = CreateVerifier();
        var code = await AuthorizeAsync(session, verifier);
        var tokens = await ExchangeAsync(session, code, verifier);

        var bearer = _fixture.CreateAnonymousClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/customers")).StatusCode);

        // Revoke the membership while the token is still inside its lifetime.
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var membership = await db.BusinessMemberships.SingleAsync(row => row.UserId == userId);
            membership.IsActive = false;
            await db.SaveChangesAsync();
        }

        // Authentication still succeeds (the token is valid); authorization refuses because the
        // current membership, not the cached claim, is the authority.
        var refused = await bearer.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ------------------------------------------------------------------ the flow helpers

    private static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Task<HttpResponseMessage> AuthorizeRawAsync(HttpClient session, string verifier, string state = "state-1")
        => session.PostAsync("/connect/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        }));

    private static async Task<string> AuthorizeAsync(HttpClient session, string verifier)
    {
        var state = Guid.NewGuid().ToString("N");
        var response = await AuthorizeRawAsync(session, verifier, state);

        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var location = response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);

        Assert.Equal(state, query["state"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["code"].ToString()));

        return query["code"].ToString();
    }

    private static Task<HttpResponseMessage> ExchangeRawAsync(HttpClient client, string code, string verifier)
        => client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier,
        }));

    private static async Task<JsonElement> ExchangeAsync(HttpClient client, string code, string verifier)
    {
        var response = await ExchangeRawAsync(client, code, verifier);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> RefreshRawAsync(HttpClient client, string refreshToken)
        => client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        }));

    private static async Task<JsonElement> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await RefreshRawAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
}
