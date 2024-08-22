using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class ActivityLog : BaseEntity
{
    public string LogName { get; set; }

    public string Description { get; set; }

    public int? SubjectId { get; set; }

    public string SubjectType { get; set; }

    public int? CauserId { get; set; }

    public string CauserType { get; set; }

    public string Properties { get; set; } // JSON string
}
