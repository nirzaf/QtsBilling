using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class CustomerGroup : BaseEntity
{
    public int BusinessId { get; set; }

    public string Name { get; set; }

    public decimal Amount { get; set; }

    public int CreatedBy { get; set; }
}
