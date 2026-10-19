using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class TypeOfService
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int BusinessId { get; set; }

    public string LocationPriceGroup { get; set; } // JSON string

    public decimal? PackingCharge { get; set; }

    public string PackingChargeType { get; set; } // "fixed" or "percent"

    public bool EnableCustomFields { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
