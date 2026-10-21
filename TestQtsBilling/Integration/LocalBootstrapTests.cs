using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Auth;
using QtsWebApp.Infrastructure;
using Xunit;

namespace TestQtsBilling.Integration;

/// <summary>
/// The operator-only local instance bootstrap (specification task 05, T05.01 - T05.03).
/// </summary>
/// <remarks>
/// Each test runs against its own disposable database migrated to the head migration, so the instance is empty
/// exactly as on a new installation. The host is the real application; the bootstrap is reached only through its
/// service entry point, which is the same code the command line calls.
/// </remarks>
public sealed class LocalBootstrapTests : IClassFixture<PostgresApiFixture>
{
    /// <summary>The head migration: the database is migrated to it and nothing else.</summary>
    private const string HeadMigration = "20261021000140_AddLocalBootstrapState";

    /// <summary>The migration before the bootstrap state was added; a database here is not ready to initialise.</summary>
    private const string PreviousHeadMigration = "20261020233420_CompleteInvoiceRecognitionSnapshots";

    private const string OwnerPassword = "Bootstrap!Passw0rd2026";

    private readonly PostgresApiFixture _fixture;

    public LocalBootstrapTests(PostgresApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task T05_01_a_migrated_empty_instance_initialises_with_the_operators_owner_and_closes_the_bootstrap()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);
        var request = NewRequest(email: "owner.one@bootstrap.test");

        var result = await LocalBootstrap.InitialiseAsync(host.Services, request);

        Assert.Equal(BootstrapStatus.Initialised, result.Status);
        await WithDbAsync(host, async (db, userManager) =>
        {
            var owner = await userManager.FindByEmailAsync("owner.one@bootstrap.test");
            Assert.NotNull(owner);
            Assert.True(await userManager.CheckPasswordAsync(owner!, OwnerPassword), "The chosen owner password must sign in.");
            Assert.False(owner!.PasswordResetRequired, "The operator chose the password, so no setup ticket is required.");

            var business = await db.Businesses.AsNoTracking().SingleAsync();
            Assert.Equal(AccountingMethod.Avco, business.AccountingMethod);
            Assert.Equal("QAR", business.BaseCurrency);
            Assert.Equal("Asia/Qatar", business.TimeZone);

            var membership = await db.BusinessMemberships.AsNoTracking().SingleAsync();
            Assert.Equal(owner.Id, membership.UserId);
            Assert.Equal(MembershipRole.Owner, membership.Role);
            Assert.True(membership.AllLocations);

            var location = await db.BusinessLocations.AsNoTracking().SingleAsync();
            Assert.Equal("HQ", location.Code);

            // The default chart carries 21 accounts; every posting role is mapped to one of them.
            Assert.Equal(21, await db.Accounts.CountAsync());

            var settings = await db.BusinessAccountingSettings.AsNoTracking().SingleAsync();
            Assert.Equal(2, settings.SettlementScale);
            var cash = await db.Accounts.AsNoTracking().SingleAsync(account => account.Code == "1000");
            Assert.Equal(cash.Id, settings.CashAccountId);

            var period = await db.AccountingPeriods.AsNoTracking().SingleAsync();
            Assert.Equal(AccountingPeriodStatus.Open, period.Status);
            Assert.Equal(1, period.StartDate.Day);

            var marker = await db.InstanceBootstraps.AsNoTracking().SingleAsync();
            Assert.Equal(request.InstanceId, marker.InstanceId);
            Assert.Equal(owner.Id, marker.FirstOwnerUserId);
            Assert.Equal(business.Id, marker.FirstBusinessId);
            Assert.Equal(nameof(BootstrapOpeningMode.NewBusiness), marker.OpeningMode);
            Assert.Equal(LocalBootstrap.CurrentSchemaVersion, marker.SchemaVersion);

            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(row => row.Action == "bootstrap");
            Assert.Equal("instance", audit.EntityType);
            Assert.DoesNotContain(OwnerPassword, audit.Summary ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("owner.one@bootstrap.test", audit.Summary ?? string.Empty, StringComparison.Ordinal);

            Assert.False(await db.Users.AnyAsync(user => user.Email == DevDataSeeder.DefaultOwnerEmail), "No development owner may be created.");
        });
    }

    [Fact]
    public async Task T05_02_two_simultaneous_bootstraps_create_exactly_one_owner_and_marker()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);
        var instanceId = Guid.NewGuid();

        var first = LocalBootstrap.InitialiseAsync(host.Services, NewRequest(instanceId, email: "racer.one@bootstrap.test"));
        var second = LocalBootstrap.InitialiseAsync(host.Services, NewRequest(instanceId, email: "racer.two@bootstrap.test"));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result.Status == BootstrapStatus.Initialised));
        Assert.Equal(1, results.Count(result => result.Status == BootstrapStatus.AlreadyInitialised));

        await WithDbAsync(host, async (db, _) =>
        {
            Assert.Equal(1, await db.InstanceBootstraps.CountAsync());
            Assert.Equal(1, await db.Businesses.CountAsync());
            Assert.Equal(1, await db.Users.CountAsync());
            Assert.Equal(1, await db.BusinessMemberships.CountAsync());
        });
    }

    [Fact]
    public async Task T05_03_a_populated_instance_without_a_marker_is_refused_with_no_writes()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);
        await WithDbAsync(host, async (db, _) =>
        {
            db.Businesses.Add(new Business { Name = "Existing trader", LegalName = "Existing trader" });
            await db.SaveChangesAsync();
        });

        var result = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "late.owner@bootstrap.test"));

        Assert.Equal(BootstrapStatus.NotEmpty, result.Status);
        await WithDbAsync(host, async (db, userManager) =>
        {
            Assert.Null(await userManager.FindByEmailAsync("late.owner@bootstrap.test"));
            Assert.Equal(0, await db.InstanceBootstraps.CountAsync());
            Assert.Equal(1, await db.Businesses.CountAsync());
            Assert.Equal(0, await db.Accounts.CountAsync());
        });
    }

    [Fact]
    public async Task T05_03_b_an_interrupted_bootstrap_leaves_no_owner_business_or_chart_and_can_be_retried()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);
        var instanceId = Guid.NewGuid();

        // The attempt fails after every row has been written, just before the commit.
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBootstrap.InitialiseAsync(
            host.Services,
            NewRequest(instanceId, email: "crash.owner@bootstrap.test"),
            _ => throw new InvalidOperationException("simulated interruption before commit")));

        await WithDbAsync(host, async (db, userManager) =>
        {
            Assert.Null(await userManager.FindByEmailAsync("crash.owner@bootstrap.test"));
            Assert.Equal(0, await db.Businesses.CountAsync());
            Assert.Equal(0, await db.BusinessLocations.CountAsync());
            Assert.Equal(0, await db.Accounts.CountAsync());
            Assert.Equal(0, await db.BusinessAccountingSettings.CountAsync());
            Assert.Equal(0, await db.AccountingPeriods.CountAsync());
            Assert.Equal(0, await db.InstanceBootstraps.CountAsync());
        });

        var retry = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(instanceId, email: "crash.owner@bootstrap.test"));
        Assert.Equal(BootstrapStatus.Initialised, retry.Status);
    }

    [Fact]
    public async Task T05_03_c_a_second_invocation_reports_already_initialised_and_changes_nothing()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);
        var instanceId = Guid.NewGuid();
        Assert.Equal(BootstrapStatus.Initialised, (await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(instanceId, email: "first.owner@bootstrap.test"))).Status);

        var again = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(instanceId, email: "second.owner@bootstrap.test", businessName: "Renamed Trader"));

        Assert.Equal(BootstrapStatus.AlreadyInitialised, again.Status);
        await WithDbAsync(host, async (db, userManager) =>
        {
            Assert.Null(await userManager.FindByEmailAsync("second.owner@bootstrap.test"));
            Assert.Equal("Bootstrap Trading LLC", (await db.Businesses.AsNoTracking().SingleAsync()).Name);
            Assert.Equal(1, await db.Users.CountAsync());
        });
    }

    [Fact]
    public async Task T05_03_d_a_different_instance_identity_is_refused_as_a_mismatch()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);
        Assert.Equal(BootstrapStatus.Initialised, (await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "owner.a@bootstrap.test"))).Status);

        var mismatch = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "owner.b@bootstrap.test"));

        Assert.Equal(BootstrapStatus.InstanceMismatch, mismatch.Status);
        await WithDbAsync(host, async (db, _) => Assert.Equal(1, await db.Users.CountAsync()));
    }

    [Fact]
    public async Task T05_03_e_demo_values_and_unsafe_settings_are_refused_before_any_write()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(HeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);

        var demoOwner = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: DevDataSeeder.DefaultOwnerEmail));
        var demoPassword = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "demo.pw@bootstrap.test") with { OwnerPassword = DevDataSeeder.DefaultOwnerPassword });
        var demoBusiness = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "demo.biz@bootstrap.test", businessName: DevDataSeeder.DefaultBusinessName));
        var badScale = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "scale@bootstrap.test") with { SettlementScale = 5 });
        var badZone = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "zone@bootstrap.test") with { TimeZone = "Mars/Olympus" });
        var notAcknowledged = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "ack@bootstrap.test") with { AcknowledgeZeroOpenings = false });

        foreach (var refusal in new[] { demoOwner, demoPassword, demoBusiness, badScale, badZone, notAcknowledged })
        {
            Assert.Equal(BootstrapStatus.InvalidRequest, refusal.Status);
        }

        await WithDbAsync(host, async (db, _) =>
        {
            Assert.Equal(0, await db.Users.CountAsync());
            Assert.Equal(0, await db.Businesses.CountAsync());
            Assert.Equal(0, await db.InstanceBootstraps.CountAsync());
        });
    }

    [Fact]
    public async Task T05_03_f_a_database_not_at_the_head_schema_is_refused_before_any_write()
    {
        await using var database = await _fixture.CreateUpgradeDatabaseAsync(PreviousHeadMigration);
        using var host = new BootstrapHost(database.ConnectionString);

        var result = await LocalBootstrap.InitialiseAsync(host.Services, NewRequest(email: "old.schema@bootstrap.test"));

        Assert.Equal(BootstrapStatus.SchemaNotMigrated, result.Status);
        await WithDbAsync(host, async (db, _) => Assert.Equal(0, await db.Users.CountAsync()));
    }

    private static BootstrapRequest NewRequest(
        Guid? instanceId = null,
        string email = "owner@bootstrap.test",
        string businessName = "Bootstrap Trading LLC")
        => new(
            instanceId ?? Guid.NewGuid(),
            email,
            OwnerPassword,
            businessName,
            businessName,
            "Head office",
            "HQ",
            "QAR",
            "Asia/Qatar",
            SettlementScale: 2,
            FiscalYearStartMonth: 1,
            OpeningMode: BootstrapOpeningMode.NewBusiness,
            AcknowledgeZeroOpenings: true);

    private static async Task WithDbAsync(BootstrapHost host, Func<BillingDbContext, UserManager<User>, Task> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await action(
            scope.ServiceProvider.GetRequiredService<BillingDbContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<User>>());
    }

    /// <summary>The real host pointed at one disposable database, without the development seeder.</summary>
    private sealed class BootstrapHost(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(PostgresServiceCollectionExtensions.ConnectionStringVariable, connectionString);
        }
    }
}
