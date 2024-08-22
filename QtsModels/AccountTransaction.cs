using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class AccountTransaction : BaseEntity
{
    public int AccountId { get; set; }

    public string Type { get; set; } // "debit" or "credit"

    public string SubType { get; set; } // "opening_balance", "fund_transfer", or "deposit"

    public decimal Amount { get; set; }

    public string ReffNo { get; set; }

    public DateTime OperationDate { get; set; }

    public int CreatedBy { get; set; }

    public int? TransactionId { get; set; }

    public int? TransactionPaymentId { get; set; }

    public int? TransferTransactionId { get; set; }

    public string Note { get; set; }
}
