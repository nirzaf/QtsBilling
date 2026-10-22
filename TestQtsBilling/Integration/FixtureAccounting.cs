using Microsoft.EntityFrameworkCore;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;

namespace TestQtsBilling.Integration;

/// <summary>
/// The chart of accounts and the accounting settings a payables journey posts through, on the fixture business
/// (specification tasks 16 and 27). The fixture starts a business with no chart on purpose - a bill must refuse
/// rather than guess an account - so the tests that walk a bill from receipt to settlement establish the mapping
/// first, the same way an implementation partner would, and then assert against the accounts they chose.
/// </summary>
public static class FixtureAccounting
{
    /// <summary>Codes the payables side recognises through, in the shape the setup screen offers them.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, AccountType Type)> PayablesChart =
        new Dictionary<string, (string, AccountType)>(StringComparer.Ordinal)
        {
            ["1000"] = ("Cash", AccountType.Asset),
            ["1010"] = ("Bank", AccountType.Asset),
            ["1100"] = ("Accounts receivable", AccountType.Asset),
            ["1200"] = ("Inventory", AccountType.Asset),
            ["1250"] = ("Delivered not invoiced", AccountType.Asset),
            ["1300"] = ("Input VAT", AccountType.Asset),
            ["2000"] = ("Accounts payable", AccountType.Liability),
            ["2100"] = ("Output VAT", AccountType.Liability),
            ["2101"] = ("GRNI", AccountType.Liability),
            ["2105"] = ("Customer deposits", AccountType.Liability),
            ["3000"] = ("Retained earnings", AccountType.Equity),
            ["4000"] = ("Sales", AccountType.Revenue),
            ["4010"] = ("Sales returns", AccountType.Revenue),
            ["5000"] = ("COGS", AccountType.Expense),
            ["5150"] = ("Purchase price variance", AccountType.Expense),
            ["5200"] = ("Inventory shrinkage", AccountType.Expense),
            ["5300"] = ("Cash over short", AccountType.Expense),
            ["5999"] = ("Rounding", AccountType.Expense),
        };

    /// <summary>Creates any missing account and points the business's accounting settings at them.</summary>
    public static async Task EnsurePayablesAsync(BillingDbContext db, Guid businessId)
    {
        var accounts = await db.Accounts
            .Where(row => row.BusinessId == businessId)
            .ToDictionaryAsync(row => row.Code, StringComparer.Ordinal);

        foreach (var (code, (name, type)) in PayablesChart)
        {
            if (accounts.ContainsKey(code))
            {
                continue;
            }

            var account = new Account { BusinessId = businessId, Code = code, Name = name, AccountType = type };
            db.Accounts.Add(account);
            accounts[code] = account;
        }

        await db.SaveChangesAsync();

        var settings = await db.BusinessAccountingSettings
            .SingleOrDefaultAsync(row => row.BusinessId == businessId);
        if (settings is null)
        {
            settings = new BusinessAccountingSettings { BusinessId = businessId };
            db.BusinessAccountingSettings.Add(settings);
        }

        settings.SettlementScale = 2;
        settings.CashAccountId = accounts["1000"].Id;
        settings.BankClearingAccountId = accounts["1010"].Id;
        settings.AccountsReceivableAccountId = accounts["1100"].Id;
        settings.InventoryAssetAccountId = accounts["1200"].Id;
        settings.DeliveredNotInvoicedAccountId = accounts["1250"].Id;
        settings.InputTaxReceivableAccountId = accounts["1300"].Id;
        settings.AccountsPayableAccountId = accounts["2000"].Id;
        settings.OutputTaxPayableAccountId = accounts["2100"].Id;
        settings.GrniClearingAccountId = accounts["2101"].Id;
        settings.CustomerDepositsAccountId = accounts["2105"].Id;
        settings.RetainedEarningsAccountId = accounts["3000"].Id;
        settings.SalesRevenueAccountId = accounts["4000"].Id;
        settings.SalesReturnsAccountId = accounts["4010"].Id;
        settings.CogsAccountId = accounts["5000"].Id;
        settings.PurchasePriceVarianceAccountId = accounts["5150"].Id;
        settings.InventoryShrinkageAccountId = accounts["5200"].Id;
        settings.CashOverShortAccountId = accounts["5300"].Id;
        settings.RoundingAccountId = accounts["5999"].Id;
        await db.SaveChangesAsync();
    }
}
