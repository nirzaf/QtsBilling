using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class TransactionSellLine
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int TransactionId { get; set; }

    public int ProductId { get; set; }

    public int VariationId { get; set; }

    public decimal Quantity { get; set; }

    public decimal QuantityReturned { get; set; }

    public decimal UnitPriceBeforeDiscount { get; set; }

    public decimal? UnitPrice { get; set; }

    public string LineDiscountType { get; set; } // "fixed" or "percentage"

    public decimal LineDiscountAmount { get; set; }

    public decimal? UnitPriceIncTax { get; set; }

    public decimal ItemTax { get; set; }

    public int? TaxId { get; set; }

    public int? DiscountId { get; set; }

    public int? LotNoLineId { get; set; }

    public string SellLineNote { get; set; }

    public int? ResServiceStaffId { get; set; }

    public string ResLineOrderStatus { get; set; }

    public int? ParentSellLineId { get; set; }

    public string ChildrenType { get; set; } // "modifier" or "combo"

    public int? SubUnitId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
