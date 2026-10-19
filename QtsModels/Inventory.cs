using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Inventory
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public required string Id { get; set; }
    public required string ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime LastUpdated { get; set; }
}