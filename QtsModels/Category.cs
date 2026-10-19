using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Category : BaseEntity
{
    public required string Name { get; set; }

    public int BusinessId { get; set; }

    public string? ShortCode { get; set; }

    public int ParentId { get; set; }

    public int CreatedBy { get; set; }

    public string? CategoryType { get; set; }

    public string? Description { get; set; }

    public string? Slug { get; set; }
}
