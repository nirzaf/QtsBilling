using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class VariationGroupPrice
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int VariationId { get; set; }

    public int PriceGroupId { get; set; }

    public decimal PriceIncTax { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
