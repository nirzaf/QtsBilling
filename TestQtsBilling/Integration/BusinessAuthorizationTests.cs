using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
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

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_user_can_select_another_active_business_and_scope_requests_to_its_membership()
    {
        var email = $"multibusiness-{Guid.NewGuid():N}@fixture.test";
        Guid userId;
        Guid firstMembershipId;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var userManager = services.GetRequiredService<UserManager<User>>();
            var db = services.GetRequiredService<BillingDbContext>();
            var user = new User
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Multi-business tester",
                IsActive = true,
                PasswordResetRequired = false,
            };
            var created = await userManager.CreateAsync(user, FixtureUsers.Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            userId = user.Id;
            var firstMembership = new BusinessMembership
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                UserId = userId,
                Role = MembershipRole.Owner,
                IsActive = true,
            };
            firstMembershipId = firstMembership.Id;
            db.BusinessMemberships.AddRange(firstMembership, new BusinessMembership
            {
                BusinessId = _fixture.BusinessB.BusinessId,
                UserId = userId,
                Role = MembershipRole.Viewer,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        var userRecord = new FixtureUser(userId, email, MembershipRole.Owner, _fixture.BusinessA.BusinessId, firstMembershipId);
        var client = await _fixture.CreateAuthenticatedClientAsync(userRecord);

        using var scopedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/locations");
        scopedRequest.Headers.Add("X-Business-Id", _fixture.BusinessB.BusinessId.ToString());
        var scopedResponse = await client.SendAsync(scopedRequest);
        Assert.Equal(HttpStatusCode.OK, scopedResponse.StatusCode);
        using var locations = JsonDocument.Parse(await scopedResponse.Content.ReadAsStringAsync());
        Assert.Contains(locations.RootElement.EnumerateArray(), item => item.GetProperty("id").GetGuid() == _fixture.BusinessB.LocationId);

        using var refusedSale = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/sales")
        {
            Content = JsonContent.Create(new
            {
                locationId = _fixture.BusinessB.LocationId,
                currency = "QAR",
                lines = Array.Empty<object>(),
                payment = new { method = "Cash", amount = 0m, cashReceived = 0m },
            }),
        };
        refusedSale.Headers.Add("X-Business-Id", _fixture.BusinessB.BusinessId.ToString());
        refusedSale.Headers.Add("Idempotency-Key", "business-scope-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(refusedSale)).StatusCode);

        var switched = await client.PostAsJsonAsync("/api/v1/auth/switch-business", new { businessId = _fixture.BusinessB.BusinessId });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        using var session = JsonDocument.Parse(await switched.Content.ReadAsStringAsync());
        Assert.Equal(_fixture.BusinessB.BusinessId, session.RootElement.GetProperty("businessId").GetGuid());
        Assert.Contains(session.RootElement.GetProperty("allowedLocationIds").EnumerateArray(), item => item.GetGuid() == _fixture.BusinessB.LocationId);
    }

    [Fact]
    public async Task Authorization_revision_invalidates_old_cookie_after_membership_scope_change()
    {
        var user = await CreateUserWithMembershipAsync(_fixture.BusinessA.BusinessId, MembershipRole.Owner);
        var client = await _fixture.CreateAuthenticatedClientAsync(user);

        await _fixture.WithFreshContextAsync(async db =>
        {
            var membership = await db.BusinessMemberships.SingleAsync(row => row.Id == user.MembershipId);
            membership.MaxDiscountRate = 0.5m;
            await db.SaveChangesAsync();
        });

        var staleCookie = await PostSaleAsync(client, _fixture.BusinessA);
        Assert.Equal(HttpStatusCode.Forbidden, staleCookie.StatusCode);

        var refreshed = await _fixture.CreateAuthenticatedClientAsync(user);
        var freshCookie = await PostSaleAsync(refreshed, _fixture.BusinessA);
        Assert.Equal(HttpStatusCode.Created, freshCookie.StatusCode);
    }

    [Fact]
    public async Task Restricted_membership_cannot_post_to_an_unassigned_location()
    {
        var extraLocationId = Guid.CreateVersion7();
        FixtureUser user;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var userManager = services.GetRequiredService<UserManager<User>>();
            var db = services.GetRequiredService<BillingDbContext>();
            var email = $"location-scope-{Guid.NewGuid():N}@fixture.test";
            var entity = new User
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "Location scope tester",
                IsActive = true,
                PasswordResetRequired = false,
            };
            var created = await userManager.CreateAsync(entity, FixtureUsers.Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

            db.BusinessLocations.Add(new BusinessLocation
            {
                Id = extraLocationId,
                BusinessId = _fixture.BusinessA.BusinessId,
                Name = "Restricted test location",
                Code = $"LOC-{Guid.NewGuid():N}",
                IsActive = true,
            });
            var membership = new BusinessMembership
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                UserId = entity.Id,
                Role = MembershipRole.Owner,
                IsActive = true,
                AllLocations = false,
            };
            db.BusinessMemberships.Add(membership);
            db.BusinessMembershipLocations.Add(new BusinessMembershipLocation
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                MembershipId = membership.Id,
                LocationId = _fixture.BusinessA.LocationId,
            });
            await db.SaveChangesAsync();
            user = new FixtureUser(entity.Id, email, MembershipRole.Owner, _fixture.BusinessA.BusinessId, membership.Id);
        }

        var client = await _fixture.CreateAuthenticatedClientAsync(user);
        Assert.Equal(HttpStatusCode.Created, (await PostSaleAsync(client, _fixture.BusinessA)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostSaleAsync(client, _fixture.BusinessA with { LocationId = extraLocationId })).StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_normalized_email_and_ip()
    {
        var client = _fixture.CreateAnonymousClient();
        var email = $"rate-limit-{Guid.NewGuid():N}@fixture.test";
        var wrong = $"Invalid-{Guid.NewGuid():N}";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = wrong });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = email.ToUpperInvariant(), password = wrong });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.StartsWith("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cash_register_create_replays_the_original_response_for_a_reused_key()
    {
        var user = await CreateUserWithMembershipAsync(_fixture.BusinessA.BusinessId, MembershipRole.Owner);
        var client = await _fixture.CreateAuthenticatedClientAsync(user);
        var key = "register-create-" + Guid.NewGuid().ToString("N");
        var name = "Idempotent register " + Guid.NewGuid().ToString("N");

        var first = await PostRegisterAsync(client, _fixture.BusinessA.LocationId, name, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstBody.RootElement.GetProperty("id").GetGuid();

        var replay = await PostRegisterAsync(client, _fixture.BusinessA.LocationId, name, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstId, replayBody.RootElement.GetProperty("id").GetGuid());

        var changed = await PostRegisterAsync(client, _fixture.BusinessA.LocationId, name + " changed", key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task Accounting_settings_validate_scale_and_reject_accounts_from_another_business()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var cash = Guid.Empty;
        var bank = Guid.Empty;
        var receivable = Guid.Empty;
        var inventory = Guid.Empty;
        var deliveredNotInvoiced = Guid.Empty;
        var taxPayable = Guid.Empty;
        var grni = Guid.Empty;
        var revenue = Guid.Empty;
        var cogs = Guid.Empty;
        var retainedEarnings = Guid.Empty;
        var configuredRevenue = Guid.Empty;
        await _fixture.WithFreshContextAsync(async db =>
        {
            var accounts = await db.Accounts.Where(row => row.BusinessId == _fixture.BusinessA.BusinessId).ToListAsync();
            cash = accounts.Single(row => row.Code == "1000").Id;
            bank = accounts.Single(row => row.Code == "1010").Id;
            receivable = accounts.Single(row => row.Code == "1100").Id;
            inventory = accounts.Single(row => row.Code == "1200").Id;
            deliveredNotInvoiced = accounts.Single(row => row.Code == "1250").Id;
            taxPayable = accounts.Single(row => row.Code == "2100").Id;
            grni = accounts.Single(row => row.Code == "2101").Id;
            revenue = accounts.Single(row => row.Code == "4000").Id;
            cogs = accounts.Single(row => row.Code == "5000").Id;
            var equity = new Account
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                Code = $"EQ-{Guid.NewGuid():N}"[..16],
                Name = "Retained earnings test account",
                AccountType = AccountType.Equity,
            };
            var revenueOverride = new Account
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                Code = $"RV-{Guid.NewGuid():N}"[..16],
                Name = "Configured sales revenue",
                AccountType = AccountType.Revenue,
            };
            db.Accounts.Add(equity);
            db.Accounts.Add(revenueOverride);
            retainedEarnings = equity.Id;
            configuredRevenue = revenueOverride.Id;
            await db.SaveChangesAsync();
        });

        var settings = new
        {
            settlementScale = 3,
            cashAccountId = cash,
            bankClearingAccountId = bank,
            accountsReceivableAccountId = receivable,
            inventoryAssetAccountId = inventory,
            deliveredNotInvoicedAccountId = deliveredNotInvoiced,
            inputTaxReceivableAccountId = inventory,
            accountsPayableAccountId = grni,
            outputTaxPayableAccountId = taxPayable,
            grniClearingAccountId = grni,
            customerDepositsAccountId = grni,
            retainedEarningsAccountId = retainedEarnings,
            salesRevenueAccountId = configuredRevenue,
            salesReturnsAccountId = revenue,
            cogsAccountId = cogs,
            purchasePriceVarianceAccountId = cogs,
            inventoryShrinkageAccountId = cogs,
            cashOverShortAccountId = cogs,
        };

        var saved = await client.PutAsJsonAsync("/api/v1/accounting/settings", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedBody = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal(3, savedBody.RootElement.GetProperty("settlementScale").GetInt32());

        var loaded = await client.GetFromJsonAsync<JsonElement>("/api/v1/accounting/settings");
        Assert.Equal(receivable, loaded.GetProperty("accountsReceivableAccountId").GetGuid());
        Assert.Equal(retainedEarnings, loaded.GetProperty("retainedEarningsAccountId").GetGuid());

        var saleResponse = await PostSaleAsync(client, _fixture.BusinessA);
        Assert.Equal(HttpStatusCode.Created, saleResponse.StatusCode);
        using var saleBody = JsonDocument.Parse(await saleResponse.Content.ReadAsStringAsync());
        var saleId = saleBody.RootElement.GetProperty("saleId").GetGuid();
        var postedToConfiguredAccount = await _fixture.WithFreshContextAsync(async db =>
            await db.JournalEntries.Where(entry => entry.BusinessId == _fixture.BusinessA.BusinessId
                    && entry.SourceType == JournalSourceType.Sale
                    && entry.SourceId == saleId)
                .Join(db.JournalLines, entry => new { entry.Id, entry.BusinessId }, line => new { Id = line.JournalEntryId, line.BusinessId },
                    (entry, line) => line)
                .AnyAsync(line => line.AccountId == configuredRevenue && line.CreditAmount > 0));
        Assert.True(postedToConfiguredAccount, "Sale revenue should use the configured account instead of a fixed chart code.");

        var foreignAccount = await _fixture.WithFreshContextAsync(async db => await db.Accounts
            .Where(row => row.BusinessId == _fixture.BusinessB.BusinessId && row.Code == "4000")
            .Select(row => row.Id)
            .FirstAsync());
        var crossBusiness = await client.PutAsJsonAsync("/api/v1/accounting/settings", new
        {
            settings.settlementScale,
            settings.cashAccountId,
            settings.bankClearingAccountId,
            settings.accountsReceivableAccountId,
            settings.inventoryAssetAccountId,
            settings.inputTaxReceivableAccountId,
            settings.accountsPayableAccountId,
            settings.outputTaxPayableAccountId,
            settings.grniClearingAccountId,
            settings.customerDepositsAccountId,
            settings.retainedEarningsAccountId,
            settings.salesRevenueAccountId,
            salesReturnsAccountId = foreignAccount,
            settings.cogsAccountId,
            settings.purchasePriceVarianceAccountId,
            settings.inventoryShrinkageAccountId,
            settings.cashOverShortAccountId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossBusiness.StatusCode);

        var invalidScale = await client.PutAsJsonAsync("/api/v1/accounting/settings", new
        {
            settlementScale = 1,
            settings.cashAccountId,
            settings.bankClearingAccountId,
            settings.accountsReceivableAccountId,
            settings.inventoryAssetAccountId,
            settings.inputTaxReceivableAccountId,
            settings.accountsPayableAccountId,
            settings.outputTaxPayableAccountId,
            settings.grniClearingAccountId,
            settings.customerDepositsAccountId,
            settings.retainedEarningsAccountId,
            settings.salesRevenueAccountId,
            settings.salesReturnsAccountId,
            settings.cogsAccountId,
            settings.purchasePriceVarianceAccountId,
            settings.inventoryShrinkageAccountId,
            settings.cashOverShortAccountId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidScale.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRegisterAsync(HttpClient client, Guid locationId, string name, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/registers")
        {
            Content = JsonContent.Create(new { locationId, name }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<FixtureUser> CreateUserWithMembershipAsync(Guid businessId, MembershipRole role)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<User>>();
        var db = services.GetRequiredService<BillingDbContext>();
        var email = $"authorization-{Guid.NewGuid():N}@fixture.test";
        var entity = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Authorization revision tester",
            IsActive = true,
            PasswordResetRequired = false,
        };
        var created = await userManager.CreateAsync(entity, FixtureUsers.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        var membership = new BusinessMembership { BusinessId = businessId, UserId = entity.Id, Role = role, IsActive = true };
        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync();
        return new FixtureUser(entity.Id, email, role, businessId, membership.Id);
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
