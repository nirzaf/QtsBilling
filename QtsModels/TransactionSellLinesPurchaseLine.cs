using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class TransactionSellLinesPurchaseLine
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int? SellLineId { get; set; }

    public int? StockAdjustmentLineId { get; set; }

    public int PurchaseLineId { get; set; }

    public decimal Quantity { get; set; }

    public decimal QtyReturned { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
