using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Invoice
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public required string Id { get; set; }

    public required string CustomerId { get; set; }
    public required DateTime Date { get; set; }
    public required List<Product> Products { get; set; }

    public required decimal TotalAmount { get; set; }
    public required InvoiceStatus Status { get; set; }
}

public enum InvoiceStatus
{
     Pending,
     Paid,
     Overdue,
     Cancelled
}
