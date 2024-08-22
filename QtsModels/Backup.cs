using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Backup : BaseEntity
{
    public DateTime BackupDate { get; set; }
    public string BackupFilePath { get; set; }
    public string Description { get; set; }
}
