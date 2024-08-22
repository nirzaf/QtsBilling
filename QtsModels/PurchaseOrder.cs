using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class PurchaseOrder
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string SupplierId { get; set; }
    public DateTime OrderDate { get; set; }
    public List<Product> Items { get; set; }
    public decimal TotalAmount { get; set; }
}
