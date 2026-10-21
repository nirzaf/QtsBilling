using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Local account provisioning and one-time handoff tickets (specification task 07, T07.01 - T07.04). No email,
/// SMS or hosted identity provider is involved, and the safeguards that keep a ticket single-use, a last owner
/// in place, and a disabled account without authority are proven against the real host and PostgreSQL.
/// </summary>
public sealed class UserLifecycleTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public UserLifecycleTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T07_01_a_ticket_sets_the_password_once_and_every_later_use_fails()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var created = await CreateCashierAsync(owner, "single-use@lifecycle.test", "Single Use Cashier");

        // The recipient chooses the password; nobody who created the account ever holds it.
        var chosen = FixtureUsers.Password + "-aB1!";
        using var anonymous = _fixture.CreateAnonymousClient();

        var first = await anonymous.PostAsJsonAsync("/api/v1/auth/setup-ticket/complete",
            new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, chosen));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var signedIn = await SignInAsync(created.User.Email, chosen);
        Assert.True(signedIn, "the recipient could not sign in with the password they chose");

        var replay = await anonymous.PostAsJsonAsync("/api/v1/auth/setup-ticket/complete",
            new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, FixtureUsers.Password + "-bB2!"));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(ApiErrorCodes.SetupTicketInvalid, CodeOf(await replay.Content.ReadAsStringAsync()));

        await _fixture.WithFreshContextAsync(async db =>
        {
            var ticket = await db.SetupTickets.AsNoTracking().SingleAsync(row => row.Id == created.Ticket.TicketId);
            Assert.NotNull(ticket.ConsumedAtUtc);
            Assert.Null(ticket.RevokedAtUtc);

            // Only the verifier survives: the stored value is a hash, never the handed-over secret.
            Assert.Equal(64, ticket.VerifierHash.Length);
            Assert.DoesNotContain(created.Ticket.Secret, ticket.VerifierHash, StringComparison.Ordinal);

            Assert.Equal(1, await db.AuditEvents.CountAsync(entry => entry.EntityType == "setup_ticket"
                && entry.EntityId == ticket.Id
                && entry.Action == "consumed"));
        });
    }

    [Fact]
    public async Task T07_01_two_concurrent_redemptions_allow_exactly_one_password_change()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var created = await CreateCashierAsync(owner, "race@lifecycle.test", "Race Cashier");

        using var clientA = _fixture.CreateAnonymousClient();
        using var clientB = _fixture.CreateAnonymousClient();

        var attempts = await Task.WhenAll(
            clientA.PostAsJsonAsync("/api/v1/auth/setup-ticket/complete",
                new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, FixtureUsers.Password + "-aB1!")),
            clientB.PostAsJsonAsync("/api/v1/auth/setup-ticket/complete",
                new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, FixtureUsers.Password + "-zY9!")));

        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Forbidden));

        await _fixture.WithFreshContextAsync(async db =>
        {
            var ticket = await db.SetupTickets.AsNoTracking().SingleAsync(row => row.Id == created.Ticket.TicketId);
            Assert.NotNull(ticket.ConsumedAtUtc);
            Assert.Equal(1, await db.AuditEvents.CountAsync(entry => entry.EntityType == "setup_ticket"
                && entry.EntityId == ticket.Id
                && entry.Action == "consumed"));
        });
    }

    [Fact]
    public async Task T07_02_an_anonymous_reset_request_reveals_neither_existence_nor_a_secret()
    {
        using var anonymous = _fixture.CreateAnonymousClient();

        var known = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request",
            new { email = _fixture.Users.OwnerA.Email });
        var unknown = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request",
            new { email = "nobody-here@lifecycle.test" });

        var knownBody = await known.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.True(string.Equals(knownBody, unknownBody, StringComparison.Ordinal),
            $"the two answers differed and reveal existence: known='{knownBody}' unknown='{unknownBody}'");
        Assert.False(knownBody.Contains("token", StringComparison.OrdinalIgnoreCase), $"the anonymous acknowledgement carried a token: {knownBody}");
    }

    [Fact]
    public async Task T07_03_the_last_owner_cannot_be_demoted_and_another_tenant_is_invisible()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var ownerMembership = await _fixture.WithFreshContextAsync(db => db.BusinessMemberships.AsNoTracking()
            .SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.UserId == _fixture.Users.OwnerA.UserId));

        var demote = await owner.PutAsJsonAsync($"/api/v1/administration/users/{_fixture.Users.OwnerA.UserId}/membership",
            new UpdateMembershipRequest(ownerMembership.Version, "Manager", true, null, null, "lock myself out"));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Contains("last active owner", await demote.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // A user of another business is not administrable here, and the response does not describe them.
        var foreign = await owner.PutAsJsonAsync($"/api/v1/administration/users/{_fixture.Users.OwnerB.UserId}/membership",
            new UpdateMembershipRequest(1, "Viewer", true, null, null, "cross tenant attempt"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using var cashier = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);
        var forbidden = await cashier.GetAsync("/api/v1/administration/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task T07_04_a_disabled_account_loses_authority_with_its_existing_session()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var created = await CreateCashierAsync(owner, "revoke-me@lifecycle.test", "Revoke Me Cashier");

        using var anonymous = _fixture.CreateAnonymousClient();
        var setup = await anonymous.PostAsJsonAsync("/api/v1/auth/setup-ticket/complete",
            new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, FixtureUsers.Password));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        using var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var signIn = await client.PostAsJsonAsync("/api/v1/auth/login",
            new Dictionary<string, string> { ["email"] = created.User.Email, ["password"] = FixtureUsers.Password });
        Assert.True(signIn.IsSuccessStatusCode, $"the new cashier could not sign in: {(int)signIn.StatusCode} {await signIn.Content.ReadAsStringAsync()}");

        var allowed = await client.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest("Pre-disable customer", null, null, null));
        Assert.True(allowed.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"a live membership was refused: {(int)allowed.StatusCode} {await allowed.Content.ReadAsStringAsync()}");

        var membershipVersion = await _fixture.WithFreshContextAsync(db => db.BusinessMemberships.AsNoTracking()
            .Where(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.UserId == created.User.UserId)
            .Select(row => row.Version)
            .SingleAsync());

        var disabled = await owner.PostAsJsonAsync($"/api/v1/administration/users/{created.User.UserId}/disable",
            new DeactivateUserRequest(membershipVersion, "left the company"));
        var disabledBody = await disabled.Content.ReadAsStringAsync();
        Assert.True(disabled.StatusCode == HttpStatusCode.OK, $"disable failed: {(int)disabled.StatusCode} {disabledBody}");

        var after = await client.PostAsJsonAsync("/api/v1/customers", new SaveCustomerRequest("Post-disable customer", null, null, null));
        Assert.True(
            after.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"a disabled account kept authority: {(int)after.StatusCode} {await after.Content.ReadAsStringAsync()}");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(AdministrationUserDto User, SetupTicketDto Ticket)> CreateCashierAsync(HttpClient owner, string email, string displayName)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/administration/users", new CreateAdministrationUserRequest(
            email,
            displayName,
            "Cashier",
            AllLocations: false,
            LocationIds: [_fixture.BusinessA.LocationId],
            // A new cashier gets no discount allowance at all (specification D13).
            MaxDiscountRate: 0m,
            Reason: "lifecycle test hire"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create failed: {(int)response.StatusCode} {body}");

        var result = await response.Content.ReadFromJsonAsync<CreateAdministrationUserResultDto>(ContractJson.Options)
            ?? throw new InvalidOperationException("the create response was empty");

        var reportedVersion = result.User.MembershipVersion;
        var membershipUserId = result.User.UserId;

        Assert.Equal("Cashier", result.User.Role);
        Assert.False(result.User.AllLocations);
        Assert.Equal(0m, result.User.MaxDiscountRate);
        Assert.True(result.User.PasswordResetRequired, "a new account must set its own password first");
        Assert.Equal(email, result.User.Email);
        Assert.True(result.Ticket.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.True(result.Ticket.Secret.Length >= 22, "the ticket secret must carry at least 128 bits");

        // A cashier may not post to the ledger, so the created account starts inside its own limits.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var membership = await db.BusinessMemberships.AsNoTracking()
                .SingleAsync(row => row.BusinessId == _fixture.BusinessA.BusinessId && row.UserId == membershipUserId);
            Assert.Equal(MembershipRole.Cashier, membership.Role);
            // The create response must carry the version the row actually has, or an operator's first edit
            // would be a false conflict.
            Assert.Equal(membership.Version, reportedVersion);
            Assert.True(await db.SetupTickets.AnyAsync(ticket => ticket.UserId == membership.UserId
                && ticket.ConsumedAtUtc == null
                && ticket.Reason == "lifecycle test hire"));
        });

        return (result.User, result.Ticket);
    }

    private async Task<bool> SignInAsync(string email, string password)
    {
        using var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new Dictionary<string, string> { ["email"] = email, ["password"] = password });

        return response.IsSuccessStatusCode;
    }

    private static string? CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
