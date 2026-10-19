using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Receipt
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string CustomerId { get; set; }
    public DateTime Date { get; set; }
    public List<Product> Items { get; set; }
    public decimal TotalAmount { get; set; }
}
