using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// Location scope of invoice reads and writes (specification task 04, T04.02).
/// </summary>
/// <remarks>
/// A membership limited to some locations must not list, read, print, issue or delete an invoice whose location it
/// cannot access. The response is the same as for a missing invoice, so another location's existence is not disclosed.
/// </remarks>
public sealed class LocationScopeTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public LocationScopeTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T04_02_a_location_limited_member_cannot_list_read_or_print_another_locations_invoice()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoiceId = await CreateDraftServiceInvoiceAsync(owner);
        var otherLocationId = await AddLocationAsync();
        await MoveDraftAsync(invoiceId, otherLocationId);

        // The cashier is limited to the fixture location, holds the invoice permission through the accountant role,
        // and the change is recorded as an authority change.
        var membershipId = _fixture.Users.Cashier.MembershipId;
        await SetMembershipAsync(membershipId, MembershipRole.Accountant, allLocations: false, grantedLocationId: _fixture.BusinessA.LocationId);
        try
        {
            var limited = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);

            var detail = await limited.GetAsync($"/api/v1/invoices/{invoiceId}");
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

            var print = await limited.GetAsync($"/api/v1/invoices/{invoiceId}/print");
            Assert.Equal(HttpStatusCode.NotFound, print.StatusCode);
            var printed = await print.Content.ReadAsStringAsync();
            Assert.DoesNotContain("model location scope", printed, StringComparison.Ordinal);

            var list = await limited.GetAsync("/api/v1/invoices?pageSize=200");
            var listBody = await list.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.DoesNotContain(invoiceId.ToString(), listBody, StringComparison.OrdinalIgnoreCase);

            // The owner, who holds every location, still reads the same invoice.
            var ownerDetail = await owner.GetAsync($"/api/v1/invoices/{invoiceId}");
            Assert.Equal(HttpStatusCode.OK, ownerDetail.StatusCode);
        }
        finally
        {
            await SetMembershipAsync(membershipId, MembershipRole.Cashier, allLocations: true, grantedLocationId: null);
        }
    }

    [Fact]
    public async Task T04_02_b_a_limited_member_cannot_issue_another_locations_draft()
    {
        var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var invoiceId = await CreateDraftServiceInvoiceAsync(owner);
        var otherLocationId = await AddLocationAsync();
        await MoveDraftAsync(invoiceId, otherLocationId);

        var membershipId = _fixture.Users.Cashier.MembershipId;
        await SetMembershipAsync(membershipId, MembershipRole.Accountant, allLocations: false, grantedLocationId: _fixture.BusinessA.LocationId);
        try
        {
            var limited = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier);

            var issue = await limited.PostAsync($"/api/v1/invoices/{invoiceId}/issue", content: null);
            Assert.Equal(HttpStatusCode.NotFound, issue.StatusCode);

            var status = await _fixture.WithFreshContextAsync(db => db.Invoices.AsNoTracking()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.Status)
                .SingleAsync());
            Assert.Equal(InvoiceStatus.Draft, status);
        }
        finally
        {
            await SetMembershipAsync(membershipId, MembershipRole.Cashier, allLocations: true, grantedLocationId: null);
        }
    }

    private async Task<Guid> CreateDraftServiceInvoiceAsync(HttpClient client)
    {
        var customerId = await CreateCustomerAsync(client);
        var request = new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            "location scope invoice",
            [new InvoiceLineRequest(null, "Service", 1m, 50m, 0m, 0m)],
            InvoiceRecognitionMode.Service);

        var create = await client.PostAsJsonAsync("/api/v1/invoices", request);
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Invoice create failed with {(int)create.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("invoice").GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddLocationAsync()
    {
        var location = new BusinessLocation
        {
            BusinessId = _fixture.BusinessA.BusinessId,
            Name = "Second branch",
            Code = $"L{Guid.NewGuid():N}"[..10],
            IsActive = true,
        };

        await _fixture.WithFreshContextAsync(async db =>
        {
            db.BusinessLocations.Add(location);
            await db.SaveChangesAsync();
        });

        return location.Id;
    }

    private Task MoveDraftAsync(Guid invoiceId, Guid locationId)
        => _fixture.WithFreshContextAsync(async db =>
        {
            var invoice = await db.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
            invoice.LocationId = locationId;
            await db.SaveChangesAsync();
        });

    /// <remarks>
    /// The role and location-scope change is saved first. A grant change then runs in its own unit of work, because
    /// the database trigger on the grant table bumps the membership version: a membership saved in the same batch
    /// would fail its optimistic concurrency check.
    /// </remarks>
    private async Task SetMembershipAsync(Guid membershipId, MembershipRole role, bool allLocations, Guid? grantedLocationId)
    {
        await _fixture.WithFreshContextAsync(async db =>
        {
            var membership = await db.BusinessMemberships.SingleAsync(candidate => candidate.Id == membershipId);
            membership.Role = role;
            membership.AllLocations = allLocations;
            await db.SaveChangesAsync();
        });

        await _fixture.WithFreshContextAsync(async db =>
        {
            var grants = await db.BusinessMembershipLocations.Where(grant => grant.MembershipId == membershipId).ToListAsync();
            db.BusinessMembershipLocations.RemoveRange(grants);

            if (grantedLocationId is { } location)
            {
                db.BusinessMembershipLocations.Add(new BusinessMembershipLocation
                {
                    BusinessId = _fixture.BusinessA.BusinessId,
                    MembershipId = membershipId,
                    LocationId = location,
                });
            }

            await db.SaveChangesAsync();
        });
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Location scope customer",
            email = $"location-scope-{Guid.NewGuid():N}@fixture.test",
            phone = $"+974{Random.Shared.Next(50_000_000, 60_000_000)}",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Customer create failed with {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetGuid();
    }
}
