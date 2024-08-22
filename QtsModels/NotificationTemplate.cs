using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class NotificationTemplate
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int BusinessId { get; set; }

    public string TemplateFor { get; set; }

    public string EmailBody { get; set; }

    public string SmsBody { get; set; }

    public string Subject { get; set; }

    public string Cc { get; set; }

    public string Bcc { get; set; }

    public bool AutoSend { get; set; }

    public bool AutoSendSms { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
