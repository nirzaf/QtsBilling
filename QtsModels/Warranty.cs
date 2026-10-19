using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Warranty
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string Name { get; set; }

    public int BusinessId { get; set; }

    public string Description { get; set; }

    public int Duration { get; set; }

    public string DurationType { get; set; } // "days", "months", or "years"

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
