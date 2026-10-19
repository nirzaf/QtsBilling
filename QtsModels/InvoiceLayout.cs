using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class InvoiceLayout
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string Name { get; set; }

    public string HeaderText { get; set; }

    public string InvoiceNoPrefix { get; set; }

    public string QuotationNoPrefix { get; set; }

    public string InvoiceHeading { get; set; }

    public string SubHeadingLine1 { get; set; }

    public string SubHeadingLine2 { get; set; }

    public string SubHeadingLine3 { get; set; }

    public string SubHeadingLine4 { get; set; }

    public string SubHeadingLine5 { get; set; }

    public string InvoiceHeadingNotPaid { get; set; }

    public string InvoiceHeadingPaid { get; set; }

    public string QuotationHeading { get; set; }

    public string SubTotalLabel { get; set; }

    public string DiscountLabel { get; set; }

    public string TaxLabel { get; set; }

    public string TotalLabel { get; set; }

    public string RoundOffLabel { get; set; }

    public string TotalDueLabel { get; set; }

    public string PaidLabel { get; set; }

    public bool ShowClientId { get; set; }

    public string ClientIdLabel { get; set; }

    public string ClientTaxLabel { get; set; }

    public string DateLabel { get; set; }

    public string DateTimeFormat { get; set; }

    public bool ShowTime { get; set; }

    public bool ShowBrand { get; set; }

    public bool ShowSku { get; set; }

    public bool ShowCatCode { get; set; }

    public bool ShowExpiry { get; set; }

    public bool ShowLot { get; set; }

    public bool ShowImage { get; set; }

    public bool ShowSaleDescription { get; set; }

    public string SalesPersonLabel { get; set; }

    public bool ShowSalesPerson { get; set; }

    public string TableProductLabel { get; set; }

    public string TableQtyLabel { get; set; }

    public string TableUnitPriceLabel { get; set; }

    public string TableSubtotalLabel { get; set; }

    public string CatCodeLabel { get; set; }

    public string Logo { get; set; }

    public bool ShowLogo { get; set; }

    public bool ShowBusinessName { get; set; }

    public bool ShowLocationName { get; set; }

    public bool ShowLandmark { get; set; }

    public bool ShowCity { get; set; }

    public bool ShowState { get; set; }

    public bool ShowZipCode { get; set; }

    public bool ShowCountry { get; set; }

    public bool ShowMobileNumber { get; set; }

    public bool ShowAlternateNumber { get; set; }

    public bool ShowEmail { get; set; }

    public bool ShowTax1 { get; set; }

    public bool ShowTax2 { get; set; }

    public bool ShowBarcode { get; set; }

    public bool ShowPayments { get; set; }

    public bool ShowCustomer { get; set; }

    public string CustomerLabel { get; set; }

    public bool ShowRewardPoint { get; set; }

    public string HighlightColor { get; set; }

    public string FooterText { get; set; }

    public string ModuleInfo { get; set; } // JSON string

    public string CommonSettings { get; set; } // JSON string

    public bool IsDefault { get; set; }

    public int BusinessId { get; set; }

    public string Design { get; set; } // "classic" or "slim"

    public string CnHeading { get; set; }

    public string CnNoLabel { get; set; }

    public string CnAmountLabel { get; set; }

    public string TableTaxHeadings { get; set; } // JSON string

    public bool ShowPreviousBal { get; set; }

    public string PrevBalLabel { get; set; }

    public string ChangeReturnLabel { get; set; }

    public string ProductCustomFields { get; set; } // JSON string

    public string ContactCustomFields { get; set; } // JSON string

    public string LocationCustomFields { get; set; } // JSON string

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
