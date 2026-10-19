using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Log
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = new ObjectId().ToString();
    public string? Message { get; set; }
    public string? StackTrace { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}