using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    public string UserType { get; set; } // "user" or "cmsn_agnt"

    public string Surname { get; set; }

    public string FirstName { get; set; }

    public string LastName { get; set; }

    public string Username { get; set; }

    public string Email { get; set; }

    public string Password { get; set; }

    public string Language { get; set; }

    public string ContactNo { get; set; }

    public string Address { get; set; }

    public string RememberToken { get; set; }

    public int? BusinessId { get; set; }

    public decimal? MaxSalesDiscountPercent { get; set; }

    public bool AllowLogin { get; set; }

    public string Status { get; set; } // "active", "inactive", or "terminated"

    public bool IsCmmnsAgnt { get; set; }

    public decimal CmmnsPercent { get; set; }

    public bool SelectedContacts { get; set; }

    public DateTime? Dob { get; set; }

    public string Gender { get; set; } // "male" or "female"

    public string MaritalStatus { get; set; } // "married", "unmarried", or "divorced"

    public string BloodGroup { get; set; }

    public string ContactNumber { get; set; }

    public string FbLink { get; set; }

    public string TwitterLink { get; set; }

    public string SocialMedia1 { get; set; }

    public string SocialMedia2 { get; set; }

    public string PermanentAddress { get; set; }

    public string CurrentAddress { get; set; }

    public string GuardianName { get; set; }

    public string CustomField1 { get; set; }

    public string CustomField2 { get; set; }

    public string CustomField3 { get; set; }

    public string CustomField4 { get; set; }

    public string BankDetails { get; set; } // JSON string

    public string IdProofName { get; set; }

    public string IdProofNumber { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
