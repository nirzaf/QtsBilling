using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Invoice
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string CustomerId { get; set; }
    public DateTime Date { get; set; }
    public List<Product> Products { get; set; }
    public decimal TotalAmount { get; set; }
    public InvoiceStatus Status { get; set; }
}

public enum InvoiceStatus
{
     Pending,
     Paid,
     Overdue,
     Cancelled
}
