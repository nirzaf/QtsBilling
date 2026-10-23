using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Accounting;
using QtsBilling.Contracts.Administration;
using QtsBilling.Contracts.Clients;
using QtsBilling.Contracts.Identity;
using QtsBilling.Contracts.Inventory;
using QtsBilling.Contracts.Serialization;
using QtsBilling.Contracts.Sync;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsBilling.Ui.Layout;
using QtsBilling.Ui.Pages.Administration;
using QtsWebApp.Features.Administration;
using QtsWebApp.Infrastructure;
using TestQtsBilling.Integration;
using Xunit;
using SyncOutcome = QtsBilling.Persistence.Postgres.SyncOutcome;

namespace TestQtsBilling.Ui;

/// <summary>
/// The administration screens (specification task 14, T14.01 - T14.03). Every call here is made through the same
/// typed client classes the shared pages inject, against the real host and a real disposable database, so a route
/// the screen cannot reach, a secret the server kept, or a money-bearing command the UI should have refused first
/// all show up as a failing test rather than an operator's surprise.
/// </summary>
public sealed class AdministrationTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    /// <summary>The disposable fixture password with the identity policy's variety added; the chosen password belongs to the recipient alone.</summary>
    private const string RecipientPassword = FixtureUsers.Password + "-t14Aa1!";

    private readonly PostgresApiFixture _fixture = fixture;

    [Fact]
    public async Task T14_01_an_owner_hires_a_cashier_with_a_one_time_code_and_that_person_signs_in_with_no_email_anywhere()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);

        // The exact class the users screen injects, not a hand-built request.
        var users = new AdministrationUserApiClient(owner);
        var setup = new BusinessSetupApiClient(owner);
        var branches = await setup.ListLocationsAsync();
        var branch = branches[0];

        var email = $"t14.cashier-{Guid.NewGuid():N}@fixture.test";
        var created = await users.CreateUserAsync(new CreateAdministrationUserRequest(
            email,
            "T14 Counter Cashier",
            "Cashier",
            AllLocations: false,
            LocationIds: [branch.Id],
            MaxDiscountRate: null,
            Reason: "hired at the counter, proven through the administration screen's own client"));

        // 1. The handover secret is long enough to be handed over in person and is only ever in this response.
        Assert.True(created.Ticket.Secret.Length >= 22, "the code carried less than 128 bits of secret");
        Assert.True(created.User.PasswordResetRequired, "a created account must not have a password the issuer knows");
        Assert.True(created.User.MaxDiscountRate == 0m, "a new cashier must hold no discount allowance, not the column default");
        Assert.Equal(new[] { branch.Id }, created.User.LocationIds);

        // 2. The list the screen reads shows the new account, searched by name rather than by identifier.
        var page = await users.ListUsersAsync(search: "T14 Counter Cashier");
        Assert.Contains(page.Items, row => row.UserId == created.User.UserId);

        // 3. The recipient, with no session and no delivery channel, sets their own password through the same
        //    client the sign-in page uses. Nothing was emailed: no email sender is even registered on this host.
        AssertNoMailTransport();

        using var recipient = _fixture.CreateAnonymousClient();
        await new AccountApiClient(recipient).CompleteSetupTicketAsync(
            new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, RecipientPassword));

        // 4. A spent code is dead, and the failure names the ticket rather than the account.
        using var secondHolder = _fixture.CreateAnonymousClient();
        var replay = await Assert.ThrowsAsync<ApiException>(() => new AccountApiClient(secondHolder)
            .CompleteSetupTicketAsync(new CompleteSetupTicketRequest(created.Ticket.TicketId, created.Ticket.Secret, RecipientPassword + "b")));
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)replay.StatusCode);
        Assert.Equal(ApiErrorCodes.SetupTicketInvalid, replay.Code);

        // 5. The new person signs in with their own password only. The password the issuer already knows is not
        //    enough to open a session, which is the whole point of the handover.
        using (var wrongPassword = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        {
            await PostgresApiFixture.AttachAntiforgeryAsync(wrongPassword);

            var refused = await wrongPassword.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, FixtureUsers.Password));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var cashier = await SignInClientAsync(email, RecipientPassword);
        var session = await new AccountApiClient(cashier).GetSessionAsync()
            ?? throw new InvalidOperationException("the hired account cannot read its own session");

        Assert.Equal(email, session.Email);
        Assert.Contains("Cashier", session.Roles);
        Assert.DoesNotContain(Permissions.UsersManage, session.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/v1/administration/users")).StatusCode);

        // 6. What the server kept is a verifier: the handed-over code appears in no stored row and no audit summary.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var ticket = await db.SetupTickets.AsNoTracking().SingleAsync(row => row.Id == created.Ticket.TicketId);
            Assert.NotNull(ticket.ConsumedAtUtc);
            Assert.Equal(64, ticket.VerifierHash.Length);
            Assert.DoesNotContain(created.Ticket.Secret, ticket.VerifierHash, StringComparison.Ordinal);
            Assert.DoesNotContain(created.Ticket.Secret, ticket.Reason, StringComparison.Ordinal);

            var summaries = await db.AuditEvents.AsNoTracking()
                .Where(entry => entry.EntityType == "business_membership" || entry.EntityType == "setup_ticket")
                .Select(entry => entry.Summary)
                .ToListAsync();

            Assert.All(summaries, summary => Assert.DoesNotContain(created.Ticket.Secret, summary, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task T14_02_a_manager_without_users_manage_gets_no_account_detail_and_no_write()
    {
        var manager = await CreateMemberAsync("t14.manager", MembershipRole.Manager);

        using var client = await _fixture.CreateAuthenticatedClientAsync(manager);
        var session = await new AccountApiClient(client).GetSessionAsync()
            ?? throw new InvalidOperationException("the manager could not read its own session");

        // 1. The role genuinely lacks the authority, and the shell therefore never offers the screen.
        Assert.DoesNotContain(Permissions.UsersManage, session.Permissions);
        Assert.DoesNotContain("Users and handover tickets", ShellNavigation.Visible(session).Select(route => route.Title));
        Assert.False(AdministrationRules.Can(session.Permissions, Permissions.UsersManage));
        Assert.Contains(Permissions.UsersManage, AdministrationRules.MissingReason("Adding a person", Permissions.UsersManage));

        // 2. A direct request is refused, and nothing sensitive crosses the wire with the refusal.
        var refused = await client.GetAsync("/api/v1/administration/users");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Equal(ApiErrorCodes.Forbidden, CodeOf(body));
        Assert.DoesNotContain("@fixture.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);

        // 3. Writes are refused before they exist: the identity that was never created stays un-created.
        var blockedEmail = $"t14.never-{Guid.NewGuid():N}@fixture.test";
        var creation = await client.PostAsJsonAsync("/api/v1/administration/users", new CreateAdministrationUserRequest(
            blockedEmail, "Blocked Write", "Cashier", AllLocations: true, LocationIds: null, MaxDiscountRate: null, Reason: "attempt without users.manage"));
        Assert.Equal(HttpStatusCode.Forbidden, creation.StatusCode);

        await _fixture.WithFreshContextAsync(async db =>
            Assert.False(await db.Users.AsNoTracking().AnyAsync(user => user.Email == blockedEmail)));

        // 4. The typed client the page injects surfaces the same refusal as a structured failure, so the screen
        //    shows the forbidden state instead of an empty list that looks like a staff-free business. The
        //    permission it asks for is the one the navigation row advertises - proved against the catalogue here
        //    and against the live route above.
        var users = new AdministrationUserApiClient(client);
        var failure = await Assert.ThrowsAsync<ApiException>(() => users.ListUsersAsync());
        Assert.True(failure.IsForbidden);
        Assert.Equal(Permissions.UsersManage, ShellNavigation.Routes
            .Single(row => row.Href == "administration/users").RequiredPermission);

        // 5. The refusal is about the permission, not the person: the same manager may administer terminals and
        //    is refused business setup, exactly as the two screens' own gates describe.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/business-settings")).StatusCode);
        Assert.True(AdministrationRules.Can(session.Permissions, Permissions.DevicesManage));
        Assert.False(AdministrationRules.Can(session.Permissions, Permissions.BusinessSettingsManage));
    }

    [Fact]
    public async Task T14_03_a_terminal_holding_unreconciled_money_cannot_move_or_retire_until_the_custody_is_resolved()
    {
        using var owner = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var devices = new DeviceAdministrationApiClient(owner);
        var setup = new BusinessSetupApiClient(owner);
        var home = (await setup.ListLocationsAsync())[0];

        var warehouse = await CreateBranchAsync(owner, "T14 Warehouse", $"T14W-{Guid.NewGuid():N}"[..12]);
        var deviceId = await EnrolAsync(owner, home.Id, "T14 till holding cash");
        await AddReceiptAsync(deviceId, SyncOutcome.NeedsReview);

        var row = await devices.GetDeviceAsync(deviceId);
        Assert.Equal(home.Id, row.LocationId);

        // 1. The screen's own reading of the row: something is owed, and both commands are explained rather than
        //    discovered by pressing them.
        Assert.True(AdministrationRules.HoldsUnresolvedMoney(row));
        Assert.Equal(1, AdministrationRules.MinimumAcknowledgedOperations(row));

        var reassignBlock = AdministrationRules.ReassignmentBlock(row);
        Assert.NotNull(reassignBlock);
        Assert.Contains("1 operation(s) awaiting review", reassignBlock, StringComparison.Ordinal);
        Assert.Contains("clearance", reassignBlock, StringComparison.OrdinalIgnoreCase);

        var retireBlock = AdministrationRules.RetirementBlock(row);
        Assert.NotNull(retireBlock);
        Assert.Contains("await review", retireBlock, StringComparison.OrdinalIgnoreCase);

        // 2. The server refuses the same two commands, and the screen's own sentence already carried the reason the
        //    refusal gives - what is warned about is what is enforced.
        var retireFailure = await Assert.ThrowsAsync<ApiException>(
            () => devices.RetireAsync(deviceId, new RetireDeviceRequest(row.Version, "hardware replaced")));
        Assert.Equal(409, retireFailure.StatusCode);
        Assert.Equal(ApiErrorCodes.ApprovalRequired, retireFailure.Code);
        Assert.Contains(retireFailure.Message, retireBlock, StringComparison.Ordinal);
        Assert.Contains("Retirement waits for reconciliation", retireFailure.Message, StringComparison.Ordinal);

        var reassignFailure = await Assert.ThrowsAsync<ApiException>(
            () => devices.ReassignAsync(deviceId, new ReassignDeviceRequest(row.Version, warehouse, Guid.NewGuid(), "just move it")));
        Assert.Equal(ApiErrorCodes.ApprovalRequired, reassignFailure.Code);

        // 3. An acknowledgement that understates what the server can see is refused outright.
        var understated = await Assert.ThrowsAsync<ApiException>(() => devices.IssueClearanceAsync(
            deviceId, new IssueReassignmentClearanceRequest(row.Version, AcknowledgedProvisionalCash: 0m, AcknowledgedOperations: 0, "nothing to reconcile")));
        Assert.Equal(409, understated.StatusCode);

        // 4. The clearance the screen records: at least the server's own count, and what the drawer holds.
        var paper = await devices.IssueClearanceAsync(deviceId, new IssueReassignmentClearanceRequest(
            row.Version,
            AcknowledgedProvisionalCash: 45.5m,
            AcknowledgedOperations: AdministrationRules.MinimumAcknowledgedOperations(row),
            "drawer counted by the duty manager and carried forward"));
        Assert.True(paper.ExpiresAtUtc > DateTimeOffset.UtcNow);

        var moved = await devices.ReassignAsync(deviceId, new ReassignDeviceRequest(
            await DeviceVersionAsync(deviceId), warehouse, paper.ClearanceId, "moved after the reconciliation was recorded"));
        Assert.Equal("Active", moved.Status);

        var after = await devices.GetDeviceAsync(deviceId);
        Assert.Equal(warehouse, after.LocationId);
        Assert.Equal(45.5m, after.DeviceHeldProvisionalCash);

        // 5. Nothing was reinterpreted: the review case still belongs to the business and branch scope it arrived
        //    under, and the paper is spent, so it cannot authorise a second move.
        await _fixture.WithFreshContextAsync(async db =>
        {
            var receipt = await db.ProcessedOperations.AsNoTracking().SingleAsync(op => op.DeviceId == deviceId);
            Assert.Equal(_fixture.BusinessA.BusinessId, receipt.BusinessId);
            Assert.Equal(SyncOutcome.NeedsReview, receipt.Outcome);

            var clearance = await db.DeviceReassignmentClearances.AsNoTracking().SingleAsync(item => item.Id == paper.ClearanceId);
            Assert.NotNull(clearance.ConsumedAtUtc);
        });

        // 5b. The spent paper cannot authorise a second move.
        var reuse = await Assert.ThrowsAsync<ApiException>(() => devices.ReassignAsync(
            deviceId, new ReassignDeviceRequest(after.Version, home.Id, paper.ClearanceId, "try the same paper twice")));
        Assert.Equal(409, reuse.StatusCode);

        // 6. A move does not settle a review case, so retirement is still refused - and the screen still says why.
        Assert.NotNull(AdministrationRules.RetirementBlock(after));
        Assert.NotNull(AdministrationRules.ReassignmentBlock(after));

        // 7. There is no routine wipe anywhere in this surface: not on the client, not on the routes, and the page
        //    states the policy instead of leaving it implied (specification D07).
        var wipeNames = typeof(IDeviceAdministrationApiClient)
            .GetMethods()
            .Select(method => method.Name)
            .Where(name => name.Contains("Wipe", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Erase", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Purge", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Delete", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(wipeNames.Length == 0, $"the terminal surface offers a destructive wipe: {string.Join(", ", wipeNames)}");

        var wipeRoutes = EndpointRoutes().Where(entry => entry.Contains("wipe", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("erase", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(wipeRoutes.Length == 0, $"the server exposes a wipe route: {string.Join(", ", wipeRoutes)}");

        var pageSource = File.ReadAllText(Path.Combine(InvoiceEditorTests.RepositoryRoot(), @"QtsBilling.Ui/Pages/Administration/Devices.razor"));
        Assert.Contains(nameof(AdministrationRules.NoWipeReason), pageSource, StringComparison.Ordinal);

        // 8. Revocation is the one command money does not block, and it destroys nothing.
        var revoked = await devices.RevokeAsync(deviceId, new RevokeDeviceRequest(after.Version, "withdrawn with its receipts intact"));
        Assert.Equal("Revoked", revoked.Status);

        await _fixture.WithFreshContextAsync(async db =>
        {
            Assert.Equal(1, await db.ProcessedOperations.AsNoTracking().CountAsync(op => op.DeviceId == deviceId));
            Assert.NotNull(await db.DeviceRegistrations.AsNoTracking().SingleAsync(device => device.Id == deviceId));
        });
    }

    [Fact]
    public async Task T14_04_what_each_administration_page_promises_is_what_the_route_actually_requires()
    {
        // A navigation row that advertises less than its route needs is how an operator ends up staring at a
        // refusal they were never warned about. The promise and the requirement are compared by asking both:
        // whatever a session is offered must answer, and whatever it is not offered must be refused.
        var expectations = new (string Href, string Title, string Route, string Permission)[]
        {
            ("administration/business", "Business and branches", "/api/v1/business-settings", Permissions.BusinessSettingsManage),
            ("administration/users", "Users and handover tickets", "/api/v1/administration/users", Permissions.UsersManage),
            ("administration/devices", "Terminals", "/api/v1/devices", Permissions.DevicesManage),
            // The two operator screens task 34 shipped read the instance itself, so they sit behind the audit
            // authority their routes actually ask for - and a role without it is refused, not offered a refusal.
            ("administration/audit", "Audit feed", "/api/v1/audit-events", Permissions.AuditView),
            ("administration/diagnostics", "Diagnostics and support bundle", "/api/v1/diagnostics", Permissions.AuditView),
        };

        string[] shipped = [.. typeof(QtsBilling.Ui.Pages.Administration.BusinessSettings).Assembly
            .GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template.Trim('/'))];

        foreach (var (href, title, route, permission) in expectations)
        {
            var row = ShellNavigation.Routes.Single(candidate => candidate.Href == href);

            // The row names the server's own permission for the underlying read, and it is usable only because a
            // component carrying that address exists in the shared library.
            Assert.Equal(title, row.Title);
            Assert.Equal(ShellAvailability.Available, row.Availability);
            Assert.Equal(permission, row.RequiredPermission);
            Assert.Contains(href, shipped);
        }

        // 1. The owner is offered all three and every route answers.
        using var ownerClient = await _fixture.CreateAuthenticatedClientAsync(_fixture.Users.OwnerA);
        var owner = await SessionAsync(ownerClient);

        foreach (var (href, _, route, permission) in expectations)
        {
            Assert.True(owner.Permissions.Contains(permission, StringComparer.Ordinal), $"the fixture owner no longer holds {permission}");
            Assert.Contains(ShellNavigation.Visible(owner), row => row.Href == href);
            Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync(route)).StatusCode);
        }

        // 2. A manager holds exactly one of the three. That one is offered and answers; the other two are neither
        //    offered nor allowed when requested directly.
        var manager = await CreateMemberAsync("t14.parity-manager", MembershipRole.Manager);
        using var managerClient = await _fixture.CreateAuthenticatedClientAsync(manager);
        var managerSession = await SessionAsync(managerClient);

        foreach (var (href, _, route, permission) in expectations)
        {
            var may = managerSession.Permissions.Contains(permission, StringComparer.Ordinal);

            Assert.Equal(may, ShellNavigation.Visible(managerSession).Any(row => row.Href == href));
            Assert.Equal(may ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await managerClient.GetAsync(route)).StatusCode);
        }

        // 3. An accountant owns none of the administration screens, yet the control-account mapping the setup
        //    screen reads is not an administration secret: its own routes answer on reports.view, which the
        //    accountant holds. The mapping save stays behind accounting.close, which a cashier does not hold.
        var accountant = await CreateMemberAsync("t14.parity-accountant", MembershipRole.Accountant);
        using var accountantClient = await _fixture.CreateAuthenticatedClientAsync(accountant);
        var accountantSession = await SessionAsync(accountantClient);

        foreach (var (href, _, route, permission) in expectations)
        {
            Assert.DoesNotContain(ShellNavigation.Visible(accountantSession).Select(row => row.Href), candidate => candidate == href);
            Assert.Equal(HttpStatusCode.Forbidden, (await accountantClient.GetAsync(route)).StatusCode);
        }

        Assert.Contains(Permissions.ReportsView, accountantSession.Permissions);
        Assert.Contains(Permissions.AccountingClose, accountantSession.Permissions);
        Assert.Equal(HttpStatusCode.OK, (await accountantClient.GetAsync("/api/v1/accounts")).StatusCode);

        // A business whose mapping has never been saved answers 404 on the settings route - which is a different
        // sentence from a refusal, and the setup screen treats it as one: it opens the slots empty for somebody
        // who may fill them in rather than failing the whole page.
        Assert.Equal(HttpStatusCode.NotFound, (await accountantClient.GetAsync("/api/v1/accounting/settings")).StatusCode);

        var cashier = await CreateMemberAsync("t14.parity-cashier", MembershipRole.Cashier);
        using var cashierClient = await _fixture.CreateAuthenticatedClientAsync(cashier);
        var cashierSession = await SessionAsync(cashierClient);

        Assert.Contains(Permissions.ReportsView, cashierSession.Permissions);
        Assert.DoesNotContain(Permissions.AccountingClose, cashierSession.Permissions);
        Assert.Equal(HttpStatusCode.NotFound, (await cashierClient.GetAsync("/api/v1/accounting/settings")).StatusCode);

        var mappingSave = await Assert.ThrowsAsync<ApiException>(() => new AccountingApiClient(cashierClient)
            .SaveSettingsAsync(new SaveBusinessAccountingSettingsRequest(
                SettlementScale: 2,
                CashAccountId: Guid.Empty, BankClearingAccountId: Guid.Empty, AccountsReceivableAccountId: Guid.Empty,
                InventoryAssetAccountId: Guid.Empty, DeliveredNotInvoicedAccountId: Guid.Empty,
                InputTaxReceivableAccountId: Guid.Empty, AccountsPayableAccountId: Guid.Empty,
                OutputTaxPayableAccountId: Guid.Empty, GrniClearingAccountId: Guid.Empty,
                CustomerDepositsAccountId: Guid.Empty, RetainedEarningsAccountId: Guid.Empty,
                SalesRevenueAccountId: Guid.Empty, SalesReturnsAccountId: Guid.Empty, CogsAccountId: Guid.Empty,
                PurchasePriceVarianceAccountId: Guid.Empty, InventoryShrinkageAccountId: Guid.Empty,
                CashOverShortAccountId: Guid.Empty)));
        Assert.True(mappingSave.IsForbidden, "the mapping save answered something other than a refusal");

        // 4. Audit and diagnostics are shipped pages, not promises: both rows are navigable, both addresses exist
        //    in the shared library, and neither one is left as a disabled link naming the task that wrote it.
        foreach (var href in new[] { "administration/audit", "administration/diagnostics" })
        {
            var row = ShellNavigation.Routes.Single(candidate => candidate.Href == href);
            Assert.Equal(ShellAvailability.Available, row.Availability);
            Assert.True(ShellNavigation.IsNavigable(row), $"{href} is offered as a page but is not navigable");
            Assert.Contains(href, shipped);
        }

        Assert.DoesNotContain(
            ShellNavigation.Routes.Where(row => row.Availability == ShellAvailability.Planned).Select(row => row.Href),
            candidate => candidate == "administration/audit");

        Assert.All(ShellNavigation.Usable(owner), row => Assert.True(
            row.RequiredPermission is null || owner.Permissions.Contains(row.RequiredPermission, StringComparer.Ordinal)));
    }

    [Fact]
    public void T14_05_every_host_that_can_render_the_administration_pages_registers_their_clients()
    {
        var root = InvoiceEditorTests.RepositoryRoot();
        var pages = new[]
        {
            typeof(QtsBilling.Ui.Pages.Administration.BusinessSettings),
            typeof(QtsBilling.Ui.Pages.Administration.Users),
            typeof(QtsBilling.Ui.Pages.Administration.Devices),
        };

        Type[] injected = [.. pages
            .SelectMany(page => page.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(property => property.PropertyType)
            .Where(type => type.IsInterface && type.Assembly == typeof(IAdministrationUserApiClient).Assembly)
            .Distinct()];

        Assert.True(injected.Length >= 4, $"the administration pages should inject the setup, user, terminal and accounting clients, found {injected.Length}");

        // 1. The server host must be able to resolve each one, because it renders these pages interactively too.
        using var scope = _fixture.Factory.Services.CreateScope();

        var missing = injected
            .Where(client => scope.ServiceProvider.GetService(client) is null)
            .Select(type => type.Name)
            .ToArray();

        Assert.True(missing.Length == 0, $"the server host renders the administration pages but registers none for: {string.Join(", ", missing)}");

        // 2. The browser and the terminal each name every client they are asked to supply.
        foreach (var startup in new[] { @"QtsWebApp/QtsWebApp.Client/Program.cs", @"QtsBilling.Maui/MauiProgram.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, startup));

            var unnamed = injected
                .Select(type => type.Name)
                .Where(name => !source.Contains(name, StringComparison.Ordinal))
                .ToArray();

            Assert.True(unnamed.Length == 0, $"{startup} routes the administration pages but never registers: {string.Join(", ", unnamed)}");
        }

        // 3. The shared library still reaches no database driver, whatever the screens now show.
        var offenders = typeof(QtsBilling.Ui.Pages.Administration.Users).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(offenders.Length == 0, $"the shared UI reached the database layer: {string.Join(", ", offenders)}");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A local handover needs no delivery channel. The proof is what the host is wired to do rather than what a
    /// namespace happens to contain: no e-mail sender is registered for Identity to use, no mail section is
    /// configured, and the only way a new account gets a password is the code the owner handed over.
    /// </summary>
    private void AssertNoMailTransport()
    {
        using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        Assert.Null(provider.GetService<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>());
        Assert.Null(provider.GetService<Microsoft.AspNetCore.Identity.IEmailSender<User>>());

        var configured = _fixture.Factory.Services
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
            .GetSection("Smtp")
            .GetChildren()
            .Count();

        Assert.Equal(0, configured);
    }

    /// <summary>Reads the session the way the shell does, through the client the pages inject.</summary>
    private static async Task<SessionDto> SessionAsync(HttpClient client)
    {
        var session = await new AccountApiClient(client).GetSessionAsync();

        return session ?? throw new InvalidOperationException("the host answered the session endpoint with nothing");
    }

    private IReadOnlyList<string> EndpointRoutes()
    {
        var sources = _fixture.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();

        return [.. sources.Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(pattern => pattern.Length > 0)];
    }

    private async Task<HttpClient> SignInClientAsync(string email, string password)
    {
        var client = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await PostgresApiFixture.AttachAntiforgeryAsync(client);

        var signIn = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));

        if (!signIn.IsSuccessStatusCode)
        {
            var body = await signIn.Content.ReadAsStringAsync();
            client.Dispose();
            throw new InvalidOperationException($"{email} could not sign in: {(int)signIn.StatusCode} {body}");
        }

        // The proof that opened the session belonged to the anonymous one; the new session carries its own.
        await PostgresApiFixture.AttachAntiforgeryAsync(client);
        return client;
    }

    private async Task<FixtureUser> CreateMemberAsync(string prefix, MembershipRole role)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@fixture.test";

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var entity = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{prefix} tester",
            IsActive = true,
            PasswordResetRequired = false,
            PreferredBusinessId = _fixture.BusinessA.BusinessId,
        };

        var created = await userManager.CreateAsync(entity, FixtureUsers.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        var membership = new BusinessMembership
        {
            BusinessId = _fixture.BusinessA.BusinessId,
            UserId = entity.Id,
            Role = role,
            IsActive = true,
            AllLocations = true,
        };

        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync();

        return new FixtureUser(entity.Id, email, role, _fixture.BusinessA.BusinessId, membership.Id);
    }

    private static async Task<Guid> CreateBranchAsync(HttpClient owner, string name, string code)
    {
        var branch = await new BusinessSetupApiClient(owner)
            .CreateLocationAsync(new CreateLocationRequest(name, code, null, null));

        return branch.Id;
    }

    private static async Task<Guid> EnrolAsync(HttpClient owner, Guid locationId, string name)
    {
        var devices = new DeviceAdministrationApiClient(owner);
        var challenge = await devices.CreateEnrolmentChallengeAsync(locationId);

        var response = await owner.PostAsJsonAsync(
            "/api/v1/devices",
            new EnrolDeviceRequest(name, "windows", locationId, challenge.EnrolmentCode, AppVersion: "1.0.0", SchemaVersion: 1),
            ContractJson.Options);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"the terminal could not be enrolled: {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("deviceId").GetGuid();
    }

    private async Task AddReceiptAsync(Guid deviceId, SyncOutcome outcome)
        => await _fixture.WithFreshContextAsync(async db =>
        {
            var operationId = Guid.NewGuid();

            db.ProcessedOperations.Add(new ProcessedOperation
            {
                BusinessId = _fixture.BusinessA.BusinessId,
                DeviceId = deviceId,
                OperationId = operationId,
                OperationType = "cash_sale",
                PayloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(operationId.ToString("D")))).ToLowerInvariant(),
                Outcome = outcome,
                ProcessedAtUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        });

    private async Task<long> DeviceVersionAsync(Guid deviceId)
        => await _fixture.WithFreshContextAsync(db => db.DeviceRegistrations.AsNoTracking()
            .Where(device => device.Id == deviceId)
            .Select(device => device.Version)
            .SingleAsync());

    private static string CodeOf(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty;
    }
}
