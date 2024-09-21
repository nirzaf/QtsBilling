using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Booking : BaseEntity
{
    public int ContactId { get; set; }

    public int? WaiterId { get; set; }

    public int? TableId { get; set; }

    public int? CorrespondentId { get; set; }

    public int BusinessId { get; set; }

    public int LocationId { get; set; }

    public DateTime BookingStart { get; set; }

    public DateTime BookingEnd { get; set; }

    public int CreatedBy { get; set; }

    public string? BookingStatus { get; set; }

    public string? BookingNote { get; set; }
}
