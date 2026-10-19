using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class StockAdjustmentLine
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int TransactionId { get; set; }

    public int ProductId { get; set; }

    public int VariationId { get; set; }

    public decimal Quantity { get; set; }

    public decimal? UnitPrice { get; set; }

    public int? RemovedPurchaseLine { get; set; }

    public int? LotNoLineId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
