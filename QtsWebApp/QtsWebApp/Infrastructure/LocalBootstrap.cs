using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Features.Auth;

namespace QtsWebApp.Infrastructure;

/// <summary>How the first business opens its books.</summary>
public enum BootstrapOpeningMode
{
    /// <summary>A new business whose opening balances are zero; the operator must acknowledge that.</summary>
    NewBusiness,

    /// <summary>Opening balances come from an approved native package (specification task 12); posting waits for it.</summary>
    PendingNativeOpening,
}

/// <summary>The operator-supplied facts of the first owner, business, location and books.</summary>
/// <remarks>
/// The owner password is never part of the request that is logged, printed or stored. The CLI obtains it from a
/// hidden terminal prompt; tests pass it directly.
/// </remarks>
public sealed record BootstrapRequest(
    Guid InstanceId,
    string OwnerEmail,
    string OwnerPassword,
    string BusinessName,
    string LegalName,
    string LocationName,
    string LocationCode,
    string BaseCurrency,
    string TimeZone,
    int SettlementScale,
    int FiscalYearStartMonth,
    BootstrapOpeningMode OpeningMode,
    bool AcknowledgeZeroOpenings = false,
    string? ApprovedOpeningManifestHash = null);

public enum BootstrapStatus
{
    /// <summary>The instance was empty and is now initialised; the bootstrap is closed permanently.</summary>
    Initialised,

    /// <summary>A completed bootstrap already exists for this instance. Nothing was changed.</summary>
    AlreadyInitialised,

    /// <summary>A completed bootstrap exists for a different instance identity. Nothing was changed.</summary>
    InstanceMismatch,

    /// <summary>The database already holds businesses or users without a bootstrap marker. Nothing was changed.</summary>
    NotEmpty,

    /// <summary>The database schema is not at the head migration. Nothing was changed.</summary>
    SchemaNotMigrated,

    /// <summary>The request failed validation or the identity rules. Nothing was changed.</summary>
    InvalidRequest,
}

public sealed record BootstrapResult(BootstrapStatus Status, string Message, Guid? BusinessId = null, Guid? OwnerUserId = null);

/// <summary>
/// The operator-only local instance bootstrap (specification task 05). It is reached only through the
/// <c>--bootstrap-instance</c> command line; there is no public setup route. It runs in one database transaction that
/// holds an advisory lock, so two concurrent invocations cannot both create an owner, and a failed or interrupted
/// attempt leaves nothing behind.
/// </summary>
public static class LocalBootstrap
{
    /// <summary>The schema version recorded in the marker; bump it with each incompatible bootstrap change.</summary>
    public const int CurrentSchemaVersion = 1;

    public const string CommandSwitch = "--bootstrap-instance";

    private const string LockStatement = "SELECT pg_advisory_xact_lock(hashtext('qts_instance_bootstrap'))";

    private static readonly int[] AllowedSettlementScales = [0, 2, 3, 4];

    /// <summary>Maps each posting role of the books to the chart code that must carry it.</summary>
    private static readonly (string Code, string Role)[] RequiredControlAccounts =
    [
        ("1000", "cash"), ("1010", "bank clearing"), ("1100", "receivable"), ("1200", "inventory"),
        ("1250", "delivered not invoiced"), ("1300", "input tax"), ("2000", "payable"), ("2100", "output tax"),
        ("2101", "GRNI"), ("2105", "customer deposits"), ("3000", "retained earnings"), ("4000", "sales revenue"),
        ("4010", "sales returns"), ("5000", "COGS"), ("5150", "purchase price variance"),
        ("5200", "inventory shrinkage"), ("5300", "cash over/short"), ("5999", "rounding"),
    ];

    /// <summary>True when the process was started for the operator bootstrap command.</summary>
    public static bool IsBootstrapCommand(string[] args) => args.Contains(CommandSwitch, StringComparer.Ordinal);

    /// <summary>
    /// Runs the bootstrap in its own scope. The <paramref name="beforeCommit"/> hook runs after every write and before
    /// the commit; a test uses it to interrupt the attempt, and an exception from it rolls the whole attempt back.
    /// </summary>
    public static async Task<BootstrapResult> InitialiseAsync(
        IServiceProvider services,
        BootstrapRequest request,
        Func<CancellationToken, Task>? beforeCommit = null,
        CancellationToken cancellationToken = default)
    {
        var invalid = Validate(request);
        if (invalid is not null)
        {
            return new BootstrapResult(BootstrapStatus.InvalidRequest, invalid);
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Serialises concurrent bootstrap attempts: the second waits here until the first has committed or rolled back.
            await db.Database.ExecuteSqlRawAsync(LockStatement, cancellationToken).ConfigureAwait(false);

            // The schema is checked before the marker is read: the marker table exists only at the head schema.
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
            {
                return new BootstrapResult(BootstrapStatus.SchemaNotMigrated, "Apply the database migrations before the bootstrap; nothing was changed.");
            }

            var marker = await db.InstanceBootstraps.AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (marker is not null)
            {
                return marker.InstanceId == request.InstanceId
                    ? new BootstrapResult(BootstrapStatus.AlreadyInitialised, "This instance was already initialised; nothing was changed.")
                    : new BootstrapResult(BootstrapStatus.InstanceMismatch, "This database belongs to a different instance; nothing was changed.");
            }

            var hasBusinesses = await db.Businesses.AnyAsync(cancellationToken).ConfigureAwait(false);
            var hasUsers = await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false);
            if (hasBusinesses || hasUsers)
            {
                return new BootstrapResult(BootstrapStatus.NotEmpty, "The database already holds business or user data without a bootstrap marker; nothing was changed.");
            }

            var now = DateTimeOffset.UtcNow;
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);

            var owner = new User
            {
                UserName = request.OwnerEmail,
                Email = request.OwnerEmail,
                DisplayName = "Owner",
                IsActive = true,
                // The owner chose this password at the operator terminal, so no setup ticket is required.
                PasswordResetRequired = false,
            };
            var created = await userManager.CreateAsync(owner, request.OwnerPassword).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                return new BootstrapResult(
                    BootstrapStatus.InvalidRequest,
                    "The owner password was rejected: " + string.Join(" ", created.Errors.Select(error => error.Description)));
            }

            var business = new Business
            {
                Name = request.BusinessName,
                LegalName = request.LegalName,
                BaseCurrency = request.BaseCurrency,
                TimeZone = request.TimeZone,
                // The supported valuation method is set explicitly; the entity default (FIFO) is not a valuation decision.
                AccountingMethod = AccountingMethod.Avco,
                IsActive = true,
            };
            db.Businesses.Add(business);
            db.BusinessLocations.Add(new BusinessLocation
            {
                BusinessId = business.Id,
                Name = request.LocationName,
                Code = request.LocationCode,
                TimeZone = request.TimeZone,
                IsActive = true,
            });
            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = business.Id,
                UserId = owner.Id,
                Role = MembershipRole.Owner,
                IsActive = true,
                AllLocations = true,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var (code, name, type) in DevDataSeeder.DefaultChart)
            {
                db.Accounts.Add(new Account
                {
                    BusinessId = business.Id,
                    Code = code,
                    Name = name,
                    AccountType = type,
                    IsActive = true,
                });
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var accounts = await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == business.Id)
                .ToDictionaryAsync(account => account.Code, cancellationToken)
                .ConfigureAwait(false);
            var controlProblem = ValidateControlAccounts(accounts);
            if (controlProblem is not null)
            {
                // The chart is written by this code, so a failure here is a programming error; the transaction is rolled back.
                throw new InvalidOperationException(controlProblem);
            }

            db.BusinessAccountingSettings.Add(new BusinessAccountingSettings
            {
                BusinessId = business.Id,
                SettlementScale = request.SettlementScale,
                CashAccountId = accounts["1000"].Id,
                BankClearingAccountId = accounts["1010"].Id,
                AccountsReceivableAccountId = accounts["1100"].Id,
                InventoryAssetAccountId = accounts["1200"].Id,
                DeliveredNotInvoicedAccountId = accounts["1250"].Id,
                InputTaxReceivableAccountId = accounts["1300"].Id,
                AccountsPayableAccountId = accounts["2000"].Id,
                OutputTaxPayableAccountId = accounts["2100"].Id,
                GrniClearingAccountId = accounts["2101"].Id,
                CustomerDepositsAccountId = accounts["2105"].Id,
                RetainedEarningsAccountId = accounts["3000"].Id,
                SalesRevenueAccountId = accounts["4000"].Id,
                SalesReturnsAccountId = accounts["4010"].Id,
                CogsAccountId = accounts["5000"].Id,
                PurchasePriceVarianceAccountId = accounts["5150"].Id,
                InventoryShrinkageAccountId = accounts["5200"].Id,
                CashOverShortAccountId = accounts["5300"].Id,
                RoundingAccountId = accounts["5999"].Id,
            });

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
            var fiscalStartYear = today.Month >= request.FiscalYearStartMonth ? today.Year : today.Year - 1;
            var fiscalStart = new DateOnly(fiscalStartYear, request.FiscalYearStartMonth, 1);
            db.AccountingPeriods.Add(new AccountingPeriod
            {
                BusinessId = business.Id,
                Name = $"FY {fiscalStartYear}",
                StartDate = fiscalStart,
                EndDate = fiscalStart.AddYears(1).AddDays(-1),
                Status = AccountingPeriodStatus.Open,
            });

            db.InstanceBootstraps.Add(new InstanceBootstrap
            {
                InstanceId = request.InstanceId,
                State = "Completed",
                SchemaVersion = CurrentSchemaVersion,
                FirstOwnerUserId = owner.Id,
                FirstBusinessId = business.Id,
                InitialisedAtUtc = now,
                OpeningMode = request.OpeningMode.ToString(),
                ApprovedOpeningManifestHash = request.ApprovedOpeningManifestHash,
            });

            db.AuditEvents.Add(new AuditEvent
            {
                BusinessId = business.Id,
                UserId = owner.Id,
                EntityType = "instance",
                EntityId = request.InstanceId,
                Action = "bootstrap",
                OccurredAtUtc = now,
                // Only non-sensitive facts; the password, the email and any secret are never recorded.
                Summary = JsonSerializer.Serialize(new
                {
                    openingMode = request.OpeningMode.ToString(),
                    settlementScale = request.SettlementScale,
                    baseCurrency = request.BaseCurrency,
                    timeZone = request.TimeZone,
                    fiscalYearStartMonth = request.FiscalYearStartMonth,
                    schemaVersion = CurrentSchemaVersion,
                }),
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new BootstrapResult(BootstrapStatus.Initialised, "The instance was initialised.", business.Id, owner.Id);
        }).ConfigureAwait(false);
    }

    /// <summary>Runs the operator command and returns the process exit code. The password is read from a hidden terminal prompt.</summary>
    public static async Task<int> RunCommandAsync(IServiceProvider services, string[] args)
    {
        if (args.Any(argument => argument.StartsWith("--owner-password", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Passwords are never accepted on the command line. The command asks for the owner password interactively.");
            return 1;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("The bootstrap needs an interactive terminal to read the owner password.");
            return 1;
        }

        Dictionary<string, string> options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        BootstrapRequest request;
        try
        {
            request = BuildRequest(options, ReadOwnerPassword());
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var result = await InitialiseAsync(services, request).ConfigureAwait(false);
        Console.WriteLine(result.Message);

        return result.Status switch
        {
            BootstrapStatus.Initialised => 0,
            BootstrapStatus.AlreadyInitialised => 2,
            BootstrapStatus.InstanceMismatch => 3,
            BootstrapStatus.NotEmpty => 4,
            BootstrapStatus.InvalidRequest => 5,
            BootstrapStatus.SchemaNotMigrated => 6,
            _ => 1,
        };
    }

    private static string ReadOwnerPassword()
    {
        var password = ReadHiddenLine("Owner password: ");
        var confirmation = ReadHiddenLine("Repeat owner password: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The two passwords do not match; nothing was changed.");
        }

        return password;
    }

    private static BootstrapRequest BuildRequest(IReadOnlyDictionary<string, string> options, string password)
    {
        if (!Guid.TryParse(Required(options, "instance-id"), out var instanceId))
        {
            throw new ArgumentException("--instance-id must be a GUID that identifies this deployment.");
        }

        if (!Enum.TryParse<BootstrapOpeningMode>(Required(options, "opening-mode"), ignoreCase: false, out var openingMode))
        {
            throw new ArgumentException("--opening-mode must be NewBusiness or PendingNativeOpening.");
        }

        var businessName = Required(options, "business-name");
        if (!int.TryParse(options.GetValueOrDefault("settlement-scale") ?? "2", out var settlementScale)
            || !int.TryParse(options.GetValueOrDefault("fiscal-year-start-month") ?? "1", out var fiscalMonth))
        {
            throw new ArgumentException("--settlement-scale and --fiscal-year-start-month must be whole numbers.");
        }

        return new BootstrapRequest(
            instanceId,
            Required(options, "owner-email").Trim(),
            password,
            businessName,
            options.GetValueOrDefault("legal-name") ?? businessName,
            Required(options, "location-name"),
            Required(options, "location-code"),
            options.GetValueOrDefault("base-currency") ?? "QAR",
            options.GetValueOrDefault("time-zone") ?? "Asia/Qatar",
            settlementScale,
            fiscalMonth,
            openingMode,
            options.ContainsKey("acknowledge-zero-openings"),
            options.GetValueOrDefault("approved-opening-manifest-hash"));
    }

    /// <summary>Rejects every value that could not safely become the first real instance.</summary>
    private static string? Validate(BootstrapRequest request)
    {
        if (request.InstanceId == Guid.Empty)
        {
            return "The instance identity is required.";
        }

        if (!IsValidEmail(request.OwnerEmail))
        {
            return "The owner email address is not valid.";
        }

        if (string.Equals(request.OwnerEmail, DevDataSeeder.DefaultOwnerEmail, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.OwnerPassword, DevDataSeeder.DefaultOwnerPassword, StringComparison.Ordinal)
            || string.Equals(request.BusinessName, DevDataSeeder.DefaultBusinessName, StringComparison.Ordinal))
        {
            return "The bootstrap refuses the development demo owner, password or business name.";
        }

        if (string.IsNullOrWhiteSpace(request.OwnerPassword))
        {
            return "An owner password is required.";
        }

        if (string.IsNullOrWhiteSpace(request.BusinessName) || request.BusinessName.Length > 200)
        {
            return "The business name is required and must be at most 200 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.LegalName) || request.LegalName.Length > 300)
        {
            return "The legal name is required and must be at most 300 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.LocationName) || request.LocationName.Length > 200)
        {
            return "The first location name is required and must be at most 200 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.LocationCode) || request.LocationCode.Length > 32)
        {
            return "The first location code is required and must be at most 32 characters.";
        }

        if (request.BaseCurrency is not { Length: 3 } || request.BaseCurrency.Any(character => character is < 'A' or > 'Z'))
        {
            return "The base currency must be a three-letter ISO 4217 code.";
        }

        if (!TryFindTimeZone(request.TimeZone))
        {
            return "The time zone is not a known IANA time zone on this server.";
        }

        if (!AllowedSettlementScales.Contains(request.SettlementScale))
        {
            return "The settlement scale must be 0, 2, 3 or 4.";
        }

        if (request.FiscalYearStartMonth is < 1 or > 12)
        {
            return "The fiscal year start month must be between 1 and 12.";
        }

        if (request.OpeningMode == BootstrapOpeningMode.NewBusiness && !request.AcknowledgeZeroOpenings)
        {
            return "A new business must acknowledge that its opening balances are zero.";
        }

        if (request.OpeningMode == BootstrapOpeningMode.PendingNativeOpening && request.AcknowledgeZeroOpenings)
        {
            return "Zero openings cannot be acknowledged for a business whose openings are pending a native package.";
        }

        return null;
    }

    private static string? ValidateControlAccounts(IReadOnlyDictionary<string, Account> accounts)
    {
        foreach (var (code, role) in RequiredControlAccounts)
        {
            if (!accounts.TryGetValue(code, out var account))
            {
                return $"The control chart has no account {code} for the {role} role.";
            }

            if (!account.IsActive)
            {
                return $"The control account {code} for the {role} role is inactive.";
            }
        }

        return null;
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320)
        {
            return false;
        }

        try
        {
            return new MailAddress(email).Address.Equals(email, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryFindTimeZone(string timeZone)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument == CommandSwitch)
            {
                continue;
            }

            var key = argument[2..];
            if (key == "acknowledge-zero-openings")
            {
                options[key] = bool.TrueString;
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"--{key} needs a value.");
            }

            options[key] = args[++index];
        }

        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key)
        => options.GetValueOrDefault(key) ?? throw new ArgumentException($"--{key} is required.");

    private static string ReadHiddenLine(string prompt)
    {
        Console.Write(prompt);
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace && builder.Length > 0)
            {
                builder.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
