using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Business : BaseEntity
{
    public string Name { get; set; }

    public int CurrencyId { get; set; }

    public DateTime? StartDate { get; set; }

    public string TaxNumber1 { get; set; }

    public string TaxLabel1 { get; set; }

    public string TaxNumber2 { get; set; }

    public string TaxLabel2 { get; set; }

    public int? DefaultSalesTax { get; set; }

    public decimal DefaultProfitPercent { get; set; }

    public int OwnerId { get; set; }

    public string TimeZone { get; set; }

    public int FyStartMonth { get; set; }

    public string AccountingMethod { get; set; } // "fifo", "lifo", or "avco"

    public decimal? DefaultSalesDiscount { get; set; }

    public string SellPriceTax { get; set; } // "includes" or "excludes"

    public string Logo { get; set; }

    public string SkuPrefix { get; set; }

    public bool EnableProductExpiry { get; set; }

    public string ExpiryType { get; set; } // "add_expiry" or "add_manufacturing"

    public string OnProductExpiry { get; set; } // "keep_selling", "stop_selling", or "auto_delete"

    public int StopSellingBefore { get; set; }

    public bool EnableTooltip { get; set; }

    public bool PurchaseInDiffCurrency { get; set; }

    public int? PurchaseCurrencyId { get; set; }

    public decimal PExchangeRate { get; set; }

    public int TransactionEditDays { get; set; }

    public int StockExpiryAlertDays { get; set; }

    public string KeyboardShortcuts { get; set; } // JSON string

    public string PosSettings { get; set; } // JSON string

    public string WeighingScaleSetting { get; set; } // JSON string

    public bool EnableBrand { get; set; }

    public bool EnableCategory { get; set; }

    public bool EnableSubCategory { get; set; }

    public bool EnablePriceTax { get; set; }

    public bool? EnablePurchaseStatus { get; set; }

    public bool EnableLotNumber { get; set; }

    public int? DefaultUnit { get; set; }

    public bool EnableSubUnits { get; set; }

    public bool EnableRacks { get; set; }

    public bool EnableRow { get; set; }

    public bool EnablePosition { get; set; }

    public bool EnableEditingProductFromPurchase { get; set; }

    public string SalesCmsnAgnt { get; set; } // "logged_in_user", "user", or "cmsn_agnt"

    public int ItemAdditionMethod { get; set; }

    public bool EnableInlineTax { get; set; }

    public string CurrencySymbolPlacement { get; set; } // "before" or "after"

    public string EnabledModules { get; set; } // JSON string

    public string DateFormat { get; set; }

    public string TimeFormat { get; set; } // "12" or "24"

    public string RefNoPrefixes { get; set; } // JSON string

    public string ThemeColor { get; set; }

    public int? CreatedBy { get; set; }

    public bool EnableRp { get; set; }

    public string RpName { get; set; }

    public decimal AmountForUnitRp { get; set; }

    public decimal MinOrderTotalForRp { get; set; }

    public int? MaxRpPerOrder { get; set; }

    public decimal RedeemAmountPerUnitRp { get; set; }

    public decimal MinOrderTotalForRedeem { get; set; }

    public int? MinRedeemPoint { get; set; }

    public int? MaxRedeemPoint { get; set; }

    public int? RpExpiryPeriod { get; set; }

    public string RpExpiryType { get; set; } // "month" or "year"

    public string EmailSettings { get; set; } // JSON string

    public string SmsSettings { get; set; } // JSON string

    public string CustomLabels { get; set; } // JSON string

    public string CommonSettings { get; set; } // JSON string

    public bool IsActive { get; set; }
}
