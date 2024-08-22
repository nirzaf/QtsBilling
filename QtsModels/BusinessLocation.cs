using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class BusinessLocation : BaseEntity
{
    public int BusinessId { get; set; }

    public string LocationId { get; set; }

    public string Name { get; set; }

    public string Landmark { get; set; }

    public string Country { get; set; }

    public string State { get; set; }

    public string City { get; set; }

    public string ZipCode { get; set; }

    public int InvoiceSchemeId { get; set; }

    public int InvoiceLayoutId { get; set; }

    public int? SaleInvoiceLayoutId { get; set; }

    public int? SellingPriceGroupId { get; set; }

    public bool PrintReceiptOnInvoice { get; set; }

    public string ReceiptPrinterType { get; set; } // "browser" or "printer"

    public int? PrinterId { get; set; }

    public string Mobile { get; set; }

    public string AlternateNumber { get; set; }

    public string Email { get; set; }

    public string Website { get; set; }

    public string FeaturedProducts { get; set; } // JSON string

    public bool IsActive { get; set; }

    public string DefaultPaymentAccounts { get; set; } // JSON string

    public string CustomField1 { get; set; }

    public string CustomField2 { get; set; }

    public string CustomField3 { get; set; }

    public string CustomField4 { get; set; }
}
