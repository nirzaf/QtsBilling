using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Account : BaseEntity
{
    public string? Name { get; set; }
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }

    public int BusinessId { get; set; }
    public string AccountNumber { get; set; }
    public int? AccountTypeId { get; set; }
    public string Note { get; set; }
    public int CreatedBy { get; set; }
    public bool IsClosed { get; set; }
}
