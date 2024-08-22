using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class DiscountVariation : BaseEntity
{
    public int DiscountId { get; set; }

    public int VariationId { get; set; }
}
