using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class DocumentAndNote : BaseEntity
{
    public int BusinessId { get; set; }

    public int NotableId { get; set; }

    public string NotableType { get; set; }

    public string Heading { get; set; }

    public string Description { get; set; }

    public bool IsPrivate { get; set; }

    public int CreatedBy { get; set; }
}
