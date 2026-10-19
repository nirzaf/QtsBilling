using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace TestQtsBilling.Integration;

/// <summary>
/// The disposable PostgreSQL/API fixture (specification task 01).
/// </summary>
/// <remarks>
/// <para>
/// The fixture owns a random <c>qts_test_…</c> database on an explicitly allowlisted test server. It
/// never falls back to the production connection, never touches a database whose name does not carry
/// the test prefix, and it only ever removes the database it created itself (it proves that with a
/// marker table).
/// </para>
/// <para>
/// The tests are not a mock: they run the real host, the real migrations and the real PostgreSQL
/// locking and constraints. A missing prerequisite fails the test; it is never reported as a pass.
/// </para>
/// </remarks>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    /// <summary>The environment variable that points at the disposable test server.</summary>
    public const string ConnectionVariable = "QTS_TEST_POSTGRES_CONNECTION";

    /// <summary>Every fixture database carries this marker; only a marked database is dropped.</summary>
    public const string MarkerTable = "qts_test_fixture_marker";

    private string _adminConnectionString = string.Empty;
    private string _databaseName = string.Empty;
    private bool _databaseCreated;

    /// <summary>The real host, pointed at the disposable database.</summary>
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>The connection string of the disposable database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The name of the disposable database.</summary>
    public string DatabaseName => _databaseName;

    /// <summary>The seeded business A (the owner of the fixture users).</summary>
    public FixtureBusiness BusinessA { get; private set; } = null!;

    /// <summary>The seeded business B (the isolation control: similar SKUs and account codes).</summary>
    public FixtureBusiness BusinessB { get; private set; } = null!;

    /// <summary>The seeded users.</summary>
    public FixtureUsers Users { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _adminConnectionString = RequireTestConnection();
        _databaseName = $"qts_test_{Guid.NewGuid():N}";

        await CreateDatabaseAsync().ConfigureAwait(false);
        _databaseCreated = true;

        ConnectionString = WithDatabase(_adminConnectionString, _databaseName);

        await ApplyMigrationsAsync().ConfigureAwait(false);
        await MarkDatabaseAsync().ConfigureAwait(false);

        Factory = new FixtureWebApplicationFactory(ConnectionString);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        BusinessA = await SeedBusinessAsync(db, "Fixture Business A", "FIX-A").ConfigureAwait(false);
        BusinessB = await SeedBusinessAsync(db, "Fixture Business B", "FIX-B").ConfigureAwait(false);
        Users = await SeedUsersAsync(scope.ServiceProvider, BusinessA, BusinessB).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();

        if (_databaseCreated)
        {
            await DropDatabaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a client that carries the session cookie of a signed-in fixture user.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(FixtureUser user)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new Dictionary<string, string>
            {
                ["email"] = user.Email,
                ["password"] = FixtureUsers.Password,
            }).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return client;
    }

    /// <summary>Creates a client for an anonymous caller.</summary>
    public HttpClient CreateAnonymousClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    /// <summary>Opens a fresh context, so a test reads the committed state and not a tracked entity.</summary>
    public async Task<T> WithFreshContextAsync<T>(Func<BillingDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        db.ChangeTracker.Clear();

        return await action(db).ConfigureAwait(false);
    }

    /// <summary>Opens a fresh context for a check that returns nothing.</summary>
    public async Task WithFreshContextAsync(Func<BillingDbContext, Task> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        db.ChangeTracker.Clear();

        await action(db).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ the safety rules

    /// <summary>
    /// Reads and validates the test connection. Every rejection happens *before* a privileged
    /// connection is opened, so a production target can never be touched.
    /// </summary>
    public static string RequireTestConnection()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{ConnectionVariable} is not set. The integration tests need a disposable PostgreSQL 18 server; " +
                "they never fall back to the production connection.");
        }

        ValidateTestConnection(configured);

        return configured;
    }

    /// <summary>
    /// The safety gate of the fixture: the target must be an allowlisted test server, the database name
    /// must be empty or a test name, and the connection must not be the production one.
    /// </summary>
    public static void ValidateTestConnection(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database ?? string.Empty;
        var host = builder.Host ?? string.Empty;

        if (database.Length > 0
            && !database.StartsWith("qts_test", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(database, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The integration tests refuse the database '{database}': only the maintenance database or a " +
                "qts_test… database may be used, and the fixture always creates its own random test database.");
        }

        var production = Environment.GetEnvironmentVariable(PostgresServiceCollectionExtensions.ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(production)
            && string.Equals(production, connectionString, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The integration tests refuse the production connection string. Point QTS_TEST_POSTGRES_CONNECTION at a disposable server.");
        }

        var allowedHosts = (Environment.GetEnvironmentVariable("QTS_TEST_POSTGRES_ALLOWED_HOSTS") ?? "localhost;127.0.0.1;::1")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The integration tests refuse the host '{host}': it is not on the allowlist " +
                $"({string.Join(", ", allowedHosts)}). Add it to QTS_TEST_POSTGRES_ALLOWED_HOSTS when it is a test server.");
        }
    }

    private static string WithDatabase(string connectionString, string database)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = database,
        };

        return builder.ConnectionString;
    }

    private async Task CreateDatabaseAsync()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = "postgres",
        };

        await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_databaseName}\";";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task ApplyMigrationsAsync()
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var db = new BillingDbContext(options);
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task MarkDatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var db = new BillingDbContext(options);
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE TABLE IF NOT EXISTS {MarkerTable} (marker text not null);").ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {MarkerTable} (marker) VALUES ('qts-billing-integration-fixture');").ConfigureAwait(false);
    }

    private async Task DropDatabaseAsync()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = "postgres",
        };

        await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        // Only a database that carries the fixture marker is removed.
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT count(*) FROM pg_database WHERE datname = @name;";
            check.Parameters.AddWithValue("name", _databaseName);

            var exists = Convert.ToInt64(await check.ExecuteScalarAsync().ConfigureAwait(false));

            if (exists == 0)
            {
                return;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ the seeded data

    private static async Task<FixtureBusiness> SeedBusinessAsync(BillingDbContext db, string name, string code)
    {
        var business = new Business
        {
            Name = name,
            LegalName = name,
            BaseCurrency = "QAR",
            TimeZone = "Asia/Qatar",
        };

        db.Businesses.Add(business);
        var location = new BusinessLocation { BusinessId = business.Id, Name = "Main", Code = $"{code}-MAIN" };
        db.BusinessLocations.Add(location);

        foreach (var (accountCode, accountName, accountType) in new (string, string, AccountType)[]
        {
            ("1000", "Cash on hand", AccountType.Asset),
            ("1010", "Bank", AccountType.Asset),
            ("1100", "Accounts receivable", AccountType.Asset),
            ("1200", "Inventory", AccountType.Asset),
            ("2100", "Tax payable", AccountType.Liability),
            ("4000", "Sales revenue", AccountType.Revenue),
            ("5000", "Cost of goods sold", AccountType.Expense),
        })
        {
            db.Accounts.Add(new Account
            {
                BusinessId = business.Id,
                Code = accountCode,
                Name = accountName,
                AccountType = accountType,
            });
        }

        var unit = new Unit { BusinessId = business.Id, Name = "Piece", ShortName = "pc", AllowDecimals = false };
        db.Units.Add(unit);

        var taxRate = new TaxRate
        {
            BusinessId = business.Id,
            Name = "VAT 15",
            Rate = 0.15m,
            IsDefault = true,
        };

        db.TaxRates.Add(taxRate);

        var product = new Product
        {
            BusinessId = business.Id,
            Name = $"Fixture product {code}",
            Sku = $"SHARED-SKU-{code}",
            TaxRateId = taxRate.Id,
            TaxType = TaxType.Exclusive,
            ProductType = ProductType.Single,
            TrackStock = true,
        };

        db.Products.Add(product);

        var variant = new ProductVariant
        {
            BusinessId = business.Id,
            ProductId = product.Id,
            Name = $"Fixture variant {code}",
            Sku = $"VAR-{code}",
            SellingPrice = 100m,
            PurchasePrice = 60m,
            IsDefault = true,
        };

        db.ProductVariants.Add(variant);

        var period = new AccountingPeriod
        {
            BusinessId = business.Id,
            Name = "Current",
            StartDate = new DateOnly(2020, 1, 1),
            EndDate = new DateOnly(2100, 12, 31),
            Status = AccountingPeriodStatus.Open,
        };

        db.AccountingPeriods.Add(period);

        // The fixture variant owns an opening stock, so a financial scenario can sell it. The balance
        // and its movement are written together: the database guard derives the balance from the
        // movements and refuses anything else.
        var now = DateTimeOffset.UtcNow;

        db.InventoryMovements.Add(new InventoryMovement
        {
            BusinessId = business.Id,
            LocationId = location.Id,
            VariantId = variant.Id,
            MovementType = MovementType.Opening,
            QuantityDelta = 100m,
            UnitCost = 60m,
            ReferenceType = "opening",
            ReferenceId = Guid.CreateVersion7(),
            OccurredAtUtc = now,
            Memo = "fixture opening stock",
        });

        db.InventoryBalances.Add(new InventoryBalance
        {
            BusinessId = business.Id,
            LocationId = location.Id,
            VariantId = variant.Id,
            OnHand = 100m,
            LastMovementAtUtc = now,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new FixtureBusiness(business.Id, location.Id, period.Id, taxRate.Id, product.Id, variant.Id, unit.Id);
    }

    private static async Task<FixtureUsers> SeedUsersAsync(IServiceProvider services, FixtureBusiness a, FixtureBusiness b)
    {
        var userManager = services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var db = services.GetRequiredService<BillingDbContext>();

        var ownerA = (await CreateUserAsync(userManager, db, "owner-a@fixture.test", "Owner A", a, MembershipRole.Owner)).Record;
        var ownerB = (await CreateUserAsync(userManager, db, "owner-b@fixture.test", "Owner B", b, MembershipRole.Owner)).Record;
        var cashier = (await CreateUserAsync(userManager, db, "cashier@fixture.test", "Cashier", a, MembershipRole.Cashier)).Record;
        var viewer = (await CreateUserAsync(userManager, db, "viewer@fixture.test", "Viewer", a, MembershipRole.Viewer)).Record;
        var (inactive, inactiveEntity) = await CreateUserAsync(userManager, db, "inactive@fixture.test", "Inactive", a, MembershipRole.Cashier);

        // The inactive identity is deactivated and its membership revoked: it must lose every authority.
        var inactiveMembership = await db.BusinessMemberships.FindAsync(inactive.MembershipId).ConfigureAwait(false);
        Assert.NotNull(inactiveMembership);
        inactiveMembership!.IsActive = false;
        await db.SaveChangesAsync().ConfigureAwait(false);

        var lockedOut = await userManager.SetLockoutEnabledAsync(inactiveEntity, true);
        lockedOut = await userManager.SetLockoutEndDateAsync(inactiveEntity, DateTimeOffset.UtcNow.AddYears(1));
        await userManager.UpdateAsync(inactiveEntity);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new FixtureUsers(ownerA, ownerB, cashier, viewer, inactive);
    }

    private static async Task<(FixtureUser Record, User Entity)> CreateUserAsync(
        Microsoft.AspNetCore.Identity.UserManager<User> userManager,
        BillingDbContext db,
        string email,
        string displayName,
        FixtureBusiness business,
        MembershipRole role)
    {
        var user = new User
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            IsActive = true,
            EmailConfirmed = true,
            // The fixture identity owns a working password, so the setup flow is not pending.
            PasswordResetRequired = false,
        };

        var created = await userManager.CreateAsync(user, FixtureUsers.Password).ConfigureAwait(false);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"The fixture could not create the user {email}: {string.Join("; ", created.Errors.Select(error => error.Description))}");
        }

        var membership = new BusinessMembership
        {
            BusinessId = business.BusinessId,
            UserId = user.Id,
            Role = role,
            IsActive = true,
        };

        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (new FixtureUser(user.Id, email, role, business.BusinessId, membership.Id), user);
    }

    /// <summary>The host, pointed at the disposable database and without the development seeder.</summary>
    private sealed class FixtureWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(PostgresServiceCollectionExtensions.ConnectionStringVariable, connectionString);
        }
    }
}

/// <summary>One seeded business with the rows a financial scenario needs.</summary>
public sealed record FixtureBusiness(
    Guid BusinessId,
    Guid LocationId,
    Guid AccountingPeriodId,
    Guid TaxRateId,
    Guid ProductId,
    Guid VariantId,
    Guid UnitId);

/// <summary>One seeded user with its membership.</summary>
public sealed record FixtureUser(Guid UserId, string Email, MembershipRole Role, Guid BusinessId, Guid MembershipId);

/// <summary>The seeded identities of the fixture.</summary>
public sealed record FixtureUsers(FixtureUser OwnerA, FixtureUser OwnerB, FixtureUser Cashier, FixtureUser Viewer, FixtureUser Inactive)
{
    /// <summary>The password of every fixture identity (a fixture credential, never a real one).</summary>
    public const string Password = "Fixture!Passw0rd2026";
}
