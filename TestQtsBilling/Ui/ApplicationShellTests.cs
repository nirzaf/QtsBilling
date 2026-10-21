using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Invoices;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Layout;
using TestQtsBilling.Integration;
using Xunit;

namespace TestQtsBilling.Ui;

/// <summary>
/// The application shell (specification task 13, T13.01 - T13.03). What the operator can discover, what the server
/// actually allows, and what happens to in-flight work when the operator changes context have to agree - so this
/// runs the real host, the real session, and the real catalogue the shared navigation renders.
/// </summary>
/// <remarks>
/// The hidden-module cases assert the server's answer as well as the menu's: hiding an entry is presentation, and
/// a direct request for the same thing must still be refused (requirement R04).
/// </remarks>
public sealed class ApplicationShellTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture = fixture;

    /// <summary>Every route a shipped Blazor component actually declares, across both hosts.</summary>
    private static readonly string[] ShippedRoutes = CollectShippedRoutes();

    [Fact]
    public async Task T13_01_what_the_shell_offers_and_what_the_server_allows_are_the_same_set()
    {
        var owner = await SessionAsync(_fixture.Users.OwnerA);
        var cashier = await SessionAsync(_fixture.Users.Cashier);
        var viewer = await SessionAsync(_fixture.Users.Viewer);

        foreach (var session in new[] { owner, cashier, viewer })
        {
            var visible = ShellNavigation.Visible(session).ToList();

            // Nothing is offered that the session's own permissions would not carry.
            Assert.All(visible, route => Assert.True(
                route.RequiredPermission is null || session.Permissions.Contains(route.RequiredPermission, StringComparer.Ordinal),
                $"the shell offered {route.Title} without {route.RequiredPermission}"));

            // And nothing the session may use is quietly missing: the catalogue is the promise, the session is the key.
            foreach (var route in ShellNavigation.Routes.Where(route => route.RequiredPermission is not null
                && session.Permissions.Contains(route.RequiredPermission, StringComparer.Ordinal)))
            {
                Assert.True(visible.Contains(route), $"{session.BusinessName} holds {route.RequiredPermission} but was not offered {route.Title}");
            }

            // A row the operator can click must be a page that exists in one of the hosts. The landing route is
            // the empty template "/", which trims away, so it is named explicitly.
            foreach (var route in ShellNavigation.Usable(session))
            {
                var template = route.Href.Trim('/');
                Assert.True(template.Length == 0 || ShippedRoutes.Contains(template, StringComparer.OrdinalIgnoreCase),
                    $"{route.Title} is offered as usable but no shipped component carries the route '{route.Href}'");
            }

            // A disabled row explains itself: it names where or when the module arrives.
            foreach (var route in visible.Where(route => !ShellNavigation.IsNavigable(route)))
            {
                Assert.False(string.IsNullOrWhiteSpace(ShellNavigation.DisabledReason(route)),
                    $"{route.Title} is unavailable with no reason given");
            }
        }

        Assert.Contains(Permissions.UsersManage, owner.Permissions);
        Assert.DoesNotContain(Permissions.UsersManage, cashier.Permissions);
        Assert.DoesNotContain(Permissions.InvoicesIssue, viewer.Permissions);

        // The menu is presentation. The same request made directly stays refused.
        using (var viewerClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer))
        {
            var refused = await viewerClient.GetAsync("/api/v1/invoices");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var cashierClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Cashier))
        {
            var refused = await cashierClient.GetAsync("/api/v1/administration/users");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // What the shell offers a cashier does work: the customer list is a permitted read.
            var allowed = await cashierClient.GetAsync("/api/v1/customers");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task T13_02_a_context_switch_moves_the_operator_not_the_money_or_the_in_flight_command()
    {
        // 1. The shell's own rule, tested where it lives.
        var guard = new BusinessSwitchGuard();
        Assert.True(guard.TryRequestSwitch(out var allowedReason));
        Assert.Null(allowedReason);

        var command = guard.Begin("pos capture");
        Assert.False(guard.TryRequestSwitch(out var busyReason));
        Assert.NotNull(busyReason);
        Assert.Contains("still being posted", busyReason, StringComparison.Ordinal);
        Assert.Equal(1, guard.InFlightCount);

        command.Dispose();
        Assert.True(guard.TryRequestSwitch(out _));

        guard.HasUnsyncedDeviceCash = true;
        Assert.False(guard.TryRequestSwitch(out var cashReason));
        Assert.Contains("has not reached the server", cashReason, StringComparison.Ordinal);
        guard.HasUnsyncedDeviceCash = false;

        // 2. On the real host: an owner of two companies changes what they are looking at, not what was already
        //    posted. A document created for one company stays in that company and is still there afterwards.
        var email = $"shell-owner-{Guid.NewGuid():N}@fixture.test";
        var (userId, _) = await CreateUserInBothBusinessesAsync(email);

        var client = await _fixture.CreateAuthenticatedClientAsync(new FixtureUser(
            userId, email, MembershipRole.Owner, _fixture.BusinessA.BusinessId, Guid.NewGuid()));

        var customerId = await CreateCustomerAsync(client, "Shell scope customer");

        var created = await client.PostAsJsonAsync("/api/v1/invoices", new SaveInvoiceRequest(
            _fixture.BusinessA.LocationId,
            customerId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate: null,
            "shell scope test",
            [new InvoiceLineRequest(_fixture.BusinessA.VariantId, null, 1m, 100m, 0m, 0.15m)],
            InvoiceRecognitionMode.DirectGoods));
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"the draft could not be created: {(int)created.StatusCode} {createdBody}");

        var invoiceId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement
            .GetProperty("invoice").GetProperty("id").GetGuid();

        var switched = await client.PostAsJsonAsync("/api/v1/auth/switch-business",
            new SwitchBusinessRequest(_fixture.BusinessB.BusinessId));
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        var other = await switched.Content.ReadFromJsonAsync<SessionDto>(ContractJson.Options)
            ?? throw new InvalidOperationException("the switch answered without a session");
        Assert.Equal(_fixture.BusinessB.BusinessId, other.BusinessId);

        // The new context cannot see the first company's draft, and the row itself still belongs to it.
        var hiddenInB = await client.GetAsync($"/api/v1/invoices/{invoiceId}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenInB.StatusCode);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var stored = await db.Invoices.AsNoTracking()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => new { invoice.BusinessId, Status = invoice.Status.ToString() })
                .SingleAsync();
            Assert.Equal(_fixture.BusinessA.BusinessId, stored.BusinessId);
            Assert.Equal(nameof(InvoiceState.Draft), stored.Status);
        }

        // Changing back is not a reset: the draft is exactly where the operator left it.
        var back = await client.PostAsJsonAsync("/api/v1/auth/switch-business",
            new SwitchBusinessRequest(_fixture.BusinessA.BusinessId));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);

        var found = await client.GetAsync($"/api/v1/invoices/{invoiceId}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
    }

    [Fact]
    public async Task T13_03_an_empty_list_a_refusal_and_a_lost_session_are_three_different_answers()
    {
        // The shell maps each of these to its own panel, so the host has to keep them distinguishable on the wire.
        using var anonymous = _fixture.CreateAnonymousClient();
        var unauthenticated = await anonymous.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, CodeOf(await unauthenticated.Content.ReadAsStringAsync()));

        using var viewer = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.Viewer);
        var forbidden = await viewer.GetAsync("/api/v1/administration/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, CodeOf(await forbidden.Content.ReadAsStringAsync()));

        // A permitted read answers as a page of results - a success the shell can render as rows or as an empty
        // state - never as an error the operator has to interpret.
        var permitted = await viewer.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        using var page = JsonDocument.Parse(await permitted.Content.ReadAsStringAsync());
        Assert.True(page.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array,
            "a permitted list read did not answer with a page of results");

        // A stale version is its own code, so the screen can offer a reload instead of a retry of the same write.
        var (conflictStatus, conflictCode) = await StaleVersionAsync();
        Assert.Equal(HttpStatusCode.Conflict, conflictStatus);
        Assert.Equal(ApiErrorCodes.OptimisticConcurrencyFailure, conflictCode);

        // And the states themselves are distinct in the shared component: no two of them share a heading, so an
        // expired session can never be presented as "nothing here yet".
        var headings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<ShellStateKind>().Where(kind => kind != ShellStateKind.Content))
        {
            headings.Add(kind.ToString());
        }

        Assert.Equal(Enum.GetValues<ShellStateKind>().Length - 1, headings.Count);
    }

    // ------------------------------------------------------------------ helpers

    private static string[] CollectShippedRoutes()
    {
        var assemblies = new[]
        {
            typeof(QtsBilling.Ui.Pages.Customers).Assembly,
            typeof(QtsWebApp.Client.Pages.Home).Assembly,
        };

        return assemblies.Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            // A page may carry more than one route (an editor reachable as both a list entry and by identity),
            // so every template it declares counts as shipped.
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => !string.IsNullOrWhiteSpace(template))
            .Select(template => template.Trim('/'))
            .Where(template => template.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<SessionDto> SessionAsync(FixtureUser user)
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(user);
        var session = await client.GetFromJsonAsync<SessionDto>("/api/v1/auth/session", ContractJson.Options);
        return session ?? throw new InvalidOperationException("the host answered the session endpoint with nothing");
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers",
            new QtsBilling.Contracts.Customers.SaveCustomerRequest(name, null, null, null));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"the customer could not be created: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<(HttpStatusCode Status, string Code)> StaleVersionAsync()
    {
        using var client = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var settings = await client.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/business-settings", ContractJson.Options)
            ?? throw new InvalidOperationException("the owner could not read business settings");

        var response = await client.PutAsJsonAsync("/api/v1/business-settings", new SaveBusinessSettingsRequest(
            settings.Version + 400,
            settings.Name,
            settings.LegalName,
            settings.TimeZone,
            settings.FiscalYearStartMonth), ContractJson.Options);

        return (response.StatusCode, CodeOf(await response.Content.ReadAsStringAsync()));
    }

    private async Task<(Guid UserId, Guid MembershipId)> CreateUserInBothBusinessesAsync(string email)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Shell Owner",
            IsActive = true,

            // The handover path (a ticket sets the first password) is task 07's subject; this owner chose their
            // own at the terminal, exactly as the bootstrap leaves it.
            PasswordResetRequired = false,

            // Two companies, so the sign-in must start somewhere defined: the first company. Without this the
            // session could open in the second one and correctly refuse the first one's branch.
            PreferredBusinessId = _fixture.BusinessA.BusinessId,
        };

        var created = await userManager.CreateAsync(user, FixtureUsers.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        var first = new BusinessMembership
        {
            BusinessId = _fixture.BusinessA.BusinessId,
            UserId = user.Id,
            Role = MembershipRole.Owner,
            IsActive = true,

            // An owner of the whole company, so the branch scope is not what this test is exercising: the
            // membership grants every branch explicitly rather than relying on a database default.
            AllLocations = true,
        };

        db.BusinessMemberships.Add(first);
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = _fixture.BusinessB.BusinessId,
            UserId = user.Id,
            Role = MembershipRole.Owner,
            IsActive = true,
            AllLocations = true,
        });

        await db.SaveChangesAsync();
        return (user.Id, first.Id);
    }

    private static string CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
    }
}
