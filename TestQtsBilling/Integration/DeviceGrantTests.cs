using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Device enrolment and the signed offline capture grant (specification task 23, finding F10).
/// </summary>
/// <remarks>
/// The grant is issued by the real host and validated by the same shared code the device runs. The
/// tests attack it the way a copied laptop or a tampered file would: wrong device, expired, forged,
/// clock rolled back, revoked device and a location that belongs to another business.
/// </remarks>
public sealed class DeviceGrantTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public DeviceGrantTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T23_01_a_grant_is_signed_and_bound_to_one_device_business_and_location()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var deviceId = await EnrolAsync(client, _fixture.BusinessA.LocationId, "Till 1");

        var (grant, keyId, publicKey, expiresAt) = await IssueGrantAsync(client, deviceId, _fixture.BusinessA.LocationId);

        var published = await client.GetAsync("/api/v1/devices/offline-grant-key");
        using var publishedDocument = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        Assert.Equal(publicKey, publishedDocument.RootElement.GetProperty("publicKey").GetString());
        Assert.Equal(keyId, publishedDocument.RootElement.GetProperty("keyId").GetString());

        // Deliberately not disposed: IdentityModel caches the signature provider by key id, and a
        // disposed key would poison the next validation that reuses the same id.
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

        var expectation = new OfflineGrant.OfflineGrantExpectation(
            deviceId,
            _fixture.BusinessA.BusinessId,
            _fixture.BusinessA.LocationId,
            RegisterSessionId: null,
            UserId: _fixture.Users.OwnerA.UserId,
            Currency: "QAR",
            RequiredPermission: Permissions.SalesPost);

        var valid = OfflineGrant.Validate(grant, rsa, keyId, expectation, DateTimeOffset.UtcNow, lastSeenAt: null);
        Assert.True(valid.IsAllowed, valid.Reason);
        Assert.Equal(deviceId, valid.Claims!.DeviceId);
        Assert.Equal(_fixture.BusinessA.BusinessId, valid.Claims.BusinessId);
        Assert.Contains(Permissions.SalesPost, valid.Claims.Permissions);

        // The same token presented by another device is worthless.
        var copied = OfflineGrant.Validate(
            grant,
            rsa,
            keyId,
            expectation with { DeviceId = Guid.CreateVersion7() },
            DateTimeOffset.UtcNow,
            lastSeenAt: null);
        Assert.False(copied.IsAllowed);
        Assert.Equal("binding", copied.Reason);

        // Expiry is enforced with no clock skew.
        var expired = OfflineGrant.Validate(
            grant,
            rsa,
            keyId,
            expectation,
            expiresAt.AddSeconds(1),
            lastSeenAt: null);
        Assert.False(expired.IsAllowed);

        // A rolled-back clock is refused even inside the lifetime.
        var rollback = OfflineGrant.Validate(
            grant,
            rsa,
            keyId,
            expectation,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            lastSeenAt: DateTimeOffset.UtcNow);
        Assert.False(rollback.IsAllowed);
        Assert.Equal("clock_rollback", rollback.Reason);

        // Flipping one character of the signature makes the grant a forgery.
        var forged = grant[..^2] + (grant[^2] == 'A' ? 'B' : 'A') + grant[^1];
        var forgedResult = OfflineGrant.Validate(forged, rsa, keyId, expectation, DateTimeOffset.UtcNow, lastSeenAt: null);
        Assert.False(forgedResult.IsAllowed);
    }

    [Fact]
    public async Task T23_02_a_revoked_device_never_receives_another_grant()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var deviceId = await EnrolAsync(client, _fixture.BusinessA.LocationId, "Till revoked");

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var device = await db.DeviceRegistrations.SingleAsync(row => row.Id == deviceId);
            device.Status = DeviceStatus.Revoked;
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            $"/api/v1/devices/{deviceId}/offline-grant",
            new { locationId = _fixture.BusinessA.LocationId, registerSessionId = (Guid?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // A cashier's own membership is not an owner, but it does hold SalesPost: the grant is issued
        // for that cashier and that device, never for the owner's wider permissions.
        var cashier = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);
        var cashierDevice = await EnrolAsync(client, _fixture.BusinessA.LocationId, "Till cashier");
        var cashierGrant = await IssueGrantAsync(cashier, cashierDevice, _fixture.BusinessA.LocationId);

        // Deliberately not disposed: IdentityModel caches the signature provider by key id, and a
        // disposed key would poison the next validation that reuses the same id.
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(cashierGrant.PublicKey), out _);

        var validated = OfflineGrant.Validate(
            cashierGrant.Grant,
            rsa,
            cashierGrant.KeyId,
            new OfflineGrant.OfflineGrantExpectation(
                cashierDevice,
                _fixture.BusinessA.BusinessId,
                _fixture.BusinessA.LocationId,
                null,
                _fixture.Users.Cashier.UserId,
                "QAR",
                Permissions.SalesPost),
            DateTimeOffset.UtcNow,
            null);

        Assert.True(validated.IsAllowed, validated.Reason);
    }

    [Fact]
    public async Task T23_03_a_location_of_another_business_is_refused_by_the_api_and_the_database()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        var response = await client.PostAsJsonAsync(
            "/api/v1/devices",
            new
            {
                name = "Cross-business till",
                platform = "windows",
                locationId = _fixture.BusinessB.LocationId,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Assert.False(await db.DeviceRegistrations.AnyAsync(row => row.Name == "Cross-business till"));

        // The database enforces the same rule even if a future endpoint forgets the check.
        await using var connection = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            insert into device_registrations (id, name, platform, status, enrolled_at_utc,
                created_at_utc, updated_at_utc, business_id, version, location_id)
            values ('{Guid.CreateVersion7()}', 'Raw cross-business till', 'windows', 'Active', now(),
                now(), now(), '{_fixture.BusinessA.BusinessId}', 0, '{_fixture.BusinessB.LocationId}');
            """;

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Guid> EnrolAsync(HttpClient client, Guid locationId, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/devices",
            new { name, platform = "windows", locationId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("deviceId").GetGuid();
    }

    private static async Task<(string Grant, string KeyId, string PublicKey, DateTimeOffset ExpiresAt)> IssueGrantAsync(
        HttpClient client,
        Guid deviceId,
        Guid locationId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/devices/{deviceId}/offline-grant",
            new { locationId, registerSessionId = (Guid?)null });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return (
            root.GetProperty("grant").GetString()!,
            root.GetProperty("keyId").GetString()!,
            root.GetProperty("publicKey").GetString()!,
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }
}
