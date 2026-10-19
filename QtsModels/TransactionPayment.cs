using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class TransactionPayment
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public int? TransactionId { get; set; }

    public int? BusinessId { get; set; }

    public bool IsReturn { get; set; }

    public decimal Amount { get; set; }

    public string Method { get; set; } // "cash", "card", "cheque", "bank_transfer", "other", "custom_pay_1", "custom_pay_2", or "custom_pay_3"

    public string TransactionNo { get; set; }

    public string CardTransactionNumber { get; set; }

    public string CardNumber { get; set; }

    public string CardType { get; set; } // "credit", "debit", or "visa"

    public string CardHolderName { get; set; }

    public string CardMonth { get; set; }

    public string CardYear { get; set; }

    public string CardSecurity { get; set; }

    public string ChequeNumber { get; set; }

    public string BankAccountNumber { get; set; }

    public DateTime? PaidOn { get; set; }

    public int CreatedBy { get; set; }

    public bool IsAdvance { get; set; }

    public int? PaymentFor { get; set; }

    public int? ParentId { get; set; }

    public string Note { get; set; }

    public string Document { get; set; }

    public string PaymentRefNo { get; set; }

    public int? AccountId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
