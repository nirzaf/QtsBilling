using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class TaxRate
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int BusinessId { get; set; }

    public string Name { get; set; }

    public decimal Amount { get; set; }

    public bool IsTaxGroup { get; set; }

    public int CreatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
