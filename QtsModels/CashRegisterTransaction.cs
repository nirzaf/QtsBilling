using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class CashRegisterTransaction : BaseEntity
{
    public int CashRegisterId { get; set; }

    public decimal Amount { get; set; }

    public string PayMethod { get; set; }

    public string Type { get; set; } // "debit" or "credit"

    public string TransactionType { get; set; } // "initial", "sell", "transfer", or "refund"

    public int? TransactionId { get; set; }
}
