using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Currency : BaseEntity
{
    public string Country { get; set; }

    public string CurrencyName { get; set; }

    public string Code { get; set; }

    public string Symbol { get; set; }

    public string ThousandSeparator { get; set; }

    public string DecimalSeparator { get; set; }
}
