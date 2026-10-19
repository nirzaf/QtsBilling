using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Sale
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public DateTime Date { get; set; }
    public string? CustomerId { get; set; }
    public List<SaleItem> Items { get; set; }
    public decimal TotalAmount { get; set; }
    public PaymentMethodType PaymentMethod { get; set; }
}