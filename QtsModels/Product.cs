using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Product
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string Name { get; set; }

    public int BusinessId { get; set; }

    public string Type { get; set; } // "single", "variable", "modifier", or "combo"

    public int? UnitId { get; set; }

    public string SubUnitIds { get; set; } // JSON string

    public int? BrandId { get; set; }

    public int? CategoryId { get; set; }

    public int? SubCategoryId { get; set; }

    public int? Tax { get; set; }

    public string TaxType { get; set; } // "inclusive" or "exclusive"

    public bool EnableStock { get; set; }

    public decimal? AlertQuantity { get; set; }

    public string Sku { get; set; }

    public string BarcodeType { get; set; } // "C39", "C128", "EAN13", "EAN8", "UPCA", or "UPCE"

    public decimal? ExpiryPeriod { get; set; }

    public string ExpiryPeriodType { get; set; } // "days" or "months"

    public bool EnableSrNo { get; set; }

    public string Weight { get; set; }

    public string ProductCustomField1 { get; set; }

    public string ProductCustomField2 { get; set; }

    public string ProductCustomField3 { get; set; }

    public string ProductCustomField4 { get; set; }

    public string Image { get; set; }

    public string ProductDescription { get; set; }
    public int Quantity { get; set; }

    public int CreatedBy { get; set; }

    public int? WarrantyId { get; set; }

    public bool IsInactive { get; set; }

    public bool NotForSelling { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
