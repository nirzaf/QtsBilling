using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class CashRegister : BaseEntity
{
    public int BusinessId { get; set; }

    public int? LocationId { get; set; }

    public int? UserId { get; set; }

    public string? Status { get; set; } // "close" or "open"

    public DateTime? ClosedAt { get; set; }

    public decimal ClosingAmount { get; set; }

    public int TotalCardSlips { get; set; }

    public int TotalCheques { get; set; }

    public string? ClosingNote { get; set; }
}
