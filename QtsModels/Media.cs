using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Media
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int BusinessId { get; set; }

    public string FileName { get; set; }

    public string Description { get; set; }

    public int? UploadedBy { get; set; }

    public string ModelType { get; set; }

    public int ModelId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
