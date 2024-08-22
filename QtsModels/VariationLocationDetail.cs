using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class VariationLocationDetail
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int ProductId { get; set; }

    public int ProductVariationId { get; set; }

    public int VariationId { get; set; }

    public int LocationId { get; set; }

    public decimal QtyAvailable { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
