using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class DashboardConfiguration : BaseEntity
{
    public int BusinessId { get; set; }

    public int CreatedBy { get; set; }

    public required string Name { get; set; }

    public string? Color { get; set; }

    public string? Configuration { get; set; } // JSON string
}
