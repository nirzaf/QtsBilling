using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Variation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string Name { get; set; }

    public int ProductId { get; set; }

    public string SubSku { get; set; }

    public int ProductVariationId { get; set; }

    public int? VariationValueId { get; set; }

    public decimal? DefaultPurchasePrice { get; set; }

    public decimal DppIncTax { get; set; }

    public decimal ProfitPercent { get; set; }

    public decimal? DefaultSellPrice { get; set; }

    public decimal? SellPriceIncTax { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string ComboVariations { get; set; } // JSON string
}
