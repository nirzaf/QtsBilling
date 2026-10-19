using QtsBilling.Contracts.Customers;

namespace QtsWebApp.Client.Models;

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

    public void Apply(CustomerDto dto)
    {
        Dto = dto;
        Name = dto.Name;
        Email = dto.Email;
        Phone = dto.Phone;
        Address = dto.Address;
        IsEditing = false;
    }

    public SaveCustomerRequest ToRequest() => new(Name, Email, Phone, Address);
}
