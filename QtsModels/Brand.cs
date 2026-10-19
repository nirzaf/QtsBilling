using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Brand : BaseEntity
{
    public int BusinessId { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public int CreatedBy { get; set; }
}
