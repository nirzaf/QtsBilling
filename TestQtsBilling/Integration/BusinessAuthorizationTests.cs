using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The authority of an actor (specification task 04, T04.01 - T04.05).
/// </summary>
/// <remarks>
/// A permission is granted by the membership the actor holds **now**, not by the claims that were issued
/// when the session was created. A revoked membership, a deactivated user and a missing permission all
/// have to lose their authority immediately (finding F07).
/// </remarks>
public sealed class BusinessAuthorizationTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public BusinessAuthorizationTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T04_01_a_revoked_membership_loses_its_authority_immediately()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);

        // The cashier may post a sale while the membership is active.
        var allowed = await PostSaleAsync(client, _fixture.BusinessA);
        Assert.True(
            allowed.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest,
            $"A cash sale was unexpectedly refused with {(int)allowed.StatusCode}: {await allowed.Content.ReadAsStringAsync()}");

        // The membership is revoked while the session stays alive.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var membership = await db.BusinessMemberships
                .FirstAsync(candidate => candidate.Id == _fixture.Users.Cashier.MembershipId);

            membership.IsActive = false;
            await db.SaveChangesAsync();
        });

        var refused = await PostSaleAsync(client, _fixture.BusinessA);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task T04_02_a_membership_without_the_permission_is_refused()
    {
        // A viewer may read, but never post a sale.
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);

        var response = await PostSaleAsync(client, _fixture.BusinessA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task T04_03_an_anonymous_caller_is_refused()
    {
        var client = _fixture.CreateAnonymousClient();

        var response = await PostSaleAsync(client, _fixture.BusinessA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task T04_04_the_owner_keeps_the_authority()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        var response = await PostSaleAsync(client, _fixture.BusinessA);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostSaleAsync(HttpClient client, FixtureBusiness business)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = business.LocationId,
                currency = "QAR",
                lines = new[]
                {
                    new
                    {
                        variantId = business.VariantId,
                        quantity = 1,
                        unitPrice = 100m,
                        discountAmount = 0m,
                        taxRate = 0.15m,
                    },
                },
                payment = new { method = "Cash", amount = 115m, cashReceived = 115m },
                note = "authorization test",
            }),
        };

        request.Headers.Add("Idempotency-Key", "auth-" + Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }
}
