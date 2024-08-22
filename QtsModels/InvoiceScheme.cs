using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class InvoiceScheme
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int BusinessId { get; set; }

    public string Name { get; set; }

    public string SchemeType { get; set; } // "blank" or "year"

    public string Prefix { get; set; }

    public int? StartNumber { get; set; }

    public int InvoiceCount { get; set; }

    public int? TotalDigits { get; set; }

    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
