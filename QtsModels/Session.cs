using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Session
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int? UserId { get; set; }

    public string IpAddress { get; set; }

    public string UserAgent { get; set; }

    public string Payload { get; set; } // JSON string

    public int LastActivity { get; set; }
}
