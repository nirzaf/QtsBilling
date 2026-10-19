using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Discount : BaseEntity
{
    public string Name { get; set; }
    public int BusinessId { get; set; }
    public int? BrandId { get; set; }
    public int? CategoryId { get; set; }
    public int? LocationId { get; set; }
    public int? Priority { get; set; }
    public string DiscountType { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool IsActive { get; set; }
    public bool ApplicableInSpg { get; set; }
    public bool ApplicableInCg { get; set; }
}
