using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Customer : BaseEntity
{
    public string? Name { get; set; } = "Customer Name";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool IsEditing { get; set; }
    public bool IsDeleted { get; set; }
}
