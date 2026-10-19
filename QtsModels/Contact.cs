using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace QtsModels;

public class Contact : BaseEntity
{
    public int BusinessId { get; set; }

    public required string Type { get; set; } // "customer" or "supplier"

    public string? SupplierBusinessName { get; set; }

    public required string Name { get; set; }

    public string? Prefix { get; set; }

    public string? FirstName { get; set; }

    public string? MiddleName { get; set; }

    public string? LastName { get; set; }

    public string? Email { get; set; }

    public required string ContactId { get; set; }

    public string? ContactStatus { get; set; } // "active", "inactive", or "terminated"

    public string? TaxNumber { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Country { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? ZipCode { get; set; }

    public DateTime? Dob { get; set; }

    public string? Mobile { get; set; }

    public string? Landline { get; set; }

    public string? AlternateNumber { get; set; }

    public int? PayTermNumber { get; set; }

    public string? PayTermType { get; set; } // "days" or "months"

    public decimal? CreditLimit { get; set; }

    public int CreatedBy { get; set; }

    public decimal Balance { get; set; }

    public int TotalRp { get; set; }

    public int TotalRpUsed { get; set; }

    public int TotalRpExpired { get; set; }

    public bool IsDefault { get; set; }

    public string? ShippingAddress { get; set; }

    public string? Position { get; set; }

    public int? CustomerGroupId { get; set; }

    public string? CustomField1 { get; set; }

    public string? CustomField2 { get; set; }

    public string? CustomField3 { get; set; }

    public string? CustomField4 { get; set; }
}
