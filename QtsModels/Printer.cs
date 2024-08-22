using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Printer
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int BusinessId { get; set; }

    public string Name { get; set; }

    public string ConnectionType { get; set; } // "network", "windows", or "linux"

    public string CapabilityProfile { get; set; } // "default", "simple", "SP2000", "TEP-200M", or "P822D"

    public string CharPerLine { get; set; }

    public string IpAddress { get; set; }

    public string Port { get; set; }

    public string Path { get; set; }

    public int CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
