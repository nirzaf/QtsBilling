using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class SystemConfiguration
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public decimal TaxRate { get; set; }
    public Dictionary<string, string> DefaultSettings { get; set; }
}
