using QtsBilling.Contracts.Customers;

namespace QtsBilling.Ui.Models;

/// <summary>Mutable add/edit model bound by the customer dialog.</summary>
public sealed class CustomerEditModel
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    public SaveCustomerRequest ToRequest() => new(Name, Email, Phone, Address);
}

/// <summary>Grid row wrapping the immutable DTO with the inline-edit buffer.</summary>
public sealed class CustomerRow
{
    public CustomerRow(CustomerDto dto)
    {
        Dto = dto;
        Apply(dto);
    }

    public CustomerDto Dto { get; private set; }

    public Guid Id => Dto.Id;

    public bool IsEditing { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    /// <summary>
    /// Set when a save was refused, most often because somebody else changed the row first. The typed values stay
    /// in this row and the note explains what to do, so a lost race is reviewed rather than quietly re-applied
    /// over another operator's correction (specification task 15, T15.03).
    /// </summary>
    public string? ConflictNote { get; set; }

    public void Apply(CustomerDto dto)
    {
        Dto = dto;
        Name = dto.Name;
        Email = dto.Email;
        Phone = dto.Phone;
        Address = dto.Address;
        IsEditing = false;
        ConflictNote = null;
    }

    public SaveCustomerRequest ToRequest() => new(Name, Email, Phone, Address);
}

/// <summary>
/// The supplier side of the same workbench (specification task 15). Deliberately a sibling rather than a shared
/// "party base class": the two records carry different rules - a customer may be owed money, a supplier is owed
/// one - and D01 asks for the smallest honest shape rather than a framework.
/// </summary>
public sealed class SupplierRow
{
    public SupplierRow(QtsBilling.Contracts.Purchasing.SupplierDto dto)
    {
        Dto = dto;
        Apply(dto);
    }

    public QtsBilling.Contracts.Purchasing.SupplierDto Dto { get; private set; }

    public Guid Id => Dto.Id;

    public bool IsEditing { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    public string? ConflictNote { get; set; }

    public void Apply(QtsBilling.Contracts.Purchasing.SupplierDto dto)
    {
        Dto = dto;
        Name = dto.Name;
        Email = dto.Email;
        Phone = dto.Phone;
        Address = dto.Address;
        IsEditing = false;
        ConflictNote = null;
    }

    public QtsBilling.Contracts.Purchasing.SaveSupplierRequest ToRequest()
        => new(Name, Email, Phone, Address);
}
