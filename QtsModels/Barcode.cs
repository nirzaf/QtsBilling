using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Barcode : BaseEntity
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public decimal Width { get; set; }

    public decimal Height { get; set; }

    public decimal PaperWidth { get; set; }

    public decimal PaperHeight { get; set; }

    public decimal TopMargin { get; set; }

    public decimal LeftMargin { get; set; }

    public decimal RowDistance { get; set; }

    public decimal ColDistance { get; set; }

    public int? StickersInOneRow { get; set; }

    public bool IsDefault { get; set; }

    public bool IsContinuous { get; set; }

    public int? StickersInOneSheet { get; set; }

    public int? BusinessId { get; set; }
}
