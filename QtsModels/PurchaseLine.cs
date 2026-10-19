using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class PurchaseLine
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int TransactionId { get; set; }

    public int ProductId { get; set; }

    public int VariationId { get; set; }

    public decimal Quantity { get; set; }

    public decimal PpWithoutDiscount { get; set; }

    public decimal DiscountPercent { get; set; }

    public decimal PurchasePrice { get; set; }

    public decimal PurchasePriceIncTax { get; set; }

    public decimal ItemTax { get; set; }

    public int? TaxId { get; set; }

    public decimal QuantitySold { get; set; }

    public decimal QuantityAdjusted { get; set; }

    public decimal QuantityReturned { get; set; }

    public decimal MfgQuantityUsed { get; set; }

    public DateTime? MfgDate { get; set; }

    public DateTime? ExpDate { get; set; }

    public string LotNumber { get; set; }

    public int? SubUnitId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
