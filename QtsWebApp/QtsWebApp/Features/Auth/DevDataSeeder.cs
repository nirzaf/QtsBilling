using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace QtsWebApp.Features.Auth;

/// <summary>
/// Development-only bootstrap (never runs in Production): applies pending migrations and creates one
/// owner account whose credentials must still be established through the reset/invitation flow, so the
/// "no usable credential until safe enrolment" rule is exercised locally exactly as in production.
/// </summary>
public static class DevDataSeeder
{
    public const string DefaultOwnerEmail = "owner@qtsbilling.local";
    public const string DefaultOwnerPassword = "QtsBilling!Dev2026";
    public const string DefaultBusinessName = "QTS Demo Business";

    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        bool isDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (!isDevelopment)
        {
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var businessName = configuration["QtsBilling:DevOwner:Business"] ?? DefaultBusinessName;
        var email = (configuration["QtsBilling:DevOwner:Email"] ?? DefaultOwnerEmail).Trim();

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Name == businessName, cancellationToken).ConfigureAwait(false);

        if (business is null)
        {
            business = new Business
            {
                Name = businessName,
                LegalName = businessName,
                BaseCurrency = "QAR",
                TimeZone = "Asia/Qatar",
            };

            db.Businesses.Add(business);
            db.BusinessLocations.Add(new BusinessLocation { BusinessId = business.Id, Name = "Main", Code = "MAIN" });
            db.Units.Add(new Unit { BusinessId = business.Id, Name = "Piece", ShortName = "pc", AllowDecimals = false });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Seeded development business {Business} ({BusinessId}).", business.Name, business.Id);
        }

        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);

        if (user is null)
        {
            user = new User
            {
                UserName = email,
                Email = email,
                DisplayName = "Development Owner",
                IsActive = true,
                PasswordResetRequired = true,
            };

            var password = configuration["QtsBilling:DevOwner:Password"] ?? DefaultOwnerPassword;
            var created = await userManager.CreateAsync(user, password).ConfigureAwait(false);

            if (!created.Succeeded)
            {
                logger.LogError(
                    "Development owner bootstrap failed: {Errors}",
                    string.Join("; ", created.Errors.Select(error => error.Description)));

                return;
            }

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = business.Id,
                UserId = user.Id,
                Role = MembershipRole.Owner,
                IsActive = true,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Seeded development owner {Email} with role Owner (account setup pending).", email);
        }

        if (user.PasswordResetRequired)
        {
            var ticket = await userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);

            logger.LogWarning(
                "Account setup ticket for {Email}: POST /api/v1/auth/password-reset/complete {{ \"email\": \"{Email}\", \"token\": \"{Ticket}\", \"newPassword\": \"<new>\" }}",
                email,
                email,
                ticket);
        }

        await SeedAccountingAsync(db, business, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Development convenience so the ledger API is immediately usable: a minimal chart of accounts, the
    /// current fiscal year as an open period and the journal number series. Production chart setup is an
    /// explicit onboarding step, not a side effect of starting the host.
    /// </summary>
    private static async Task SeedAccountingAsync(
        BillingDbContext db,
        Business business,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var existingCodes = await db.Accounts
            .Where(account => account.BusinessId == business.Id)
            .Select(account => account.Code)
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var (code, name, type) in DefaultChart.Where(account => !existingCodes.Contains(account.Code)))
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

        var fiscalYear = DateTime.UtcNow.Year;

        if (!await db.AccountingPeriods.AnyAsync(period => period.BusinessId == business.Id, cancellationToken).ConfigureAwait(false))
        {
            db.AccountingPeriods.Add(new AccountingPeriod
            {
                BusinessId = business.Id,
                Name = $"FY {fiscalYear}",
                StartDate = new DateOnly(fiscalYear, 1, 1),
                EndDate = new DateOnly(fiscalYear, 12, 31),
                Status = AccountingPeriodStatus.Open,
            });

            logger.LogInformation("Seeded open accounting period FY {Year} for {Business}.", fiscalYear, business.Name);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!await db.BusinessAccountingSettings.AnyAsync(settings => settings.BusinessId == business.Id, cancellationToken).ConfigureAwait(false))
        {
            var accounts = await db.Accounts.AsNoTracking()
                .Where(account => account.BusinessId == business.Id)
                .ToDictionaryAsync(account => account.Code, cancellationToken)
                .ConfigureAwait(false);
            db.BusinessAccountingSettings.Add(new BusinessAccountingSettings
            {
                BusinessId = business.Id,
                SettlementScale = 2,
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
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The default chart of accounts; shared with the operator bootstrap, which seeds the same codes.</summary>
    internal static readonly (string Code, string Name, AccountType Type)[] DefaultChart =
    [
        ("1000", "Cash on Hand", AccountType.Asset),
        ("1010", "Bank", AccountType.Asset),
        ("1100", "Accounts Receivable", AccountType.Asset),
        ("1200", "Inventory", AccountType.Asset),
        ("1250", "Delivered Goods Not Invoiced", AccountType.Asset),
        ("1210", "Inventory In Transit", AccountType.Asset),
        ("1300", "Input Tax Receivable", AccountType.Asset),
        ("2000", "Accounts Payable", AccountType.Liability),
        ("2100", "Tax Payable", AccountType.Liability),
        ("2101", "Goods Received Not Invoiced", AccountType.Liability),
        ("2105", "Customer Deposits", AccountType.Liability),
        ("3000", "Owner Equity", AccountType.Equity),
        ("4000", "Sales Revenue", AccountType.Revenue),
        ("4010", "Sales Returns", AccountType.Revenue),
        ("4100", "Sales Discounts", AccountType.Revenue),
        ("5000", "Cost of Goods Sold", AccountType.Expense),
        ("5150", "Purchase Price Variance", AccountType.Expense),
        ("5200", "Inventory Shrinkage", AccountType.Expense),
        ("5300", "Cash Over Short", AccountType.Expense),
        ("5999", "Rounding Differences", AccountType.Expense),
        ("5100", "Operating Expenses", AccountType.Expense),
    ];
}
