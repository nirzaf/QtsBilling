using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class ModelHasPermission
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int PermissionId { get; set; }

    public string ModelType { get; set; }

    public int ModelId { get; set; }
}
