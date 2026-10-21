namespace QtsBilling.Ui.Models;

/// <summary>
/// One thing an operator may pick by name: a branch, an account, a supplier, a product. The identifier travels
/// with it, but no screen ever asks a human to read or type one (specification requirement R07).
/// </summary>
/// <param name="Id">The server's identifier, chosen by the picker rather than typed.</param>
/// <param name="Name">What the operator recognises.</param>
/// <param name="Detail">A disambiguating second line - a code, an address, a balance - never a GUID.</param>
public sealed record ReferenceItem(Guid Id, string Name, string? Detail)
{
    public string Label => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name} ({Detail})";

    public static ReferenceItem From(Guid id, string name, string? detail = null) => new(id, name, detail);
}

/// <summary>
/// One control-account slot on the setup screen. <paramref name="ExpectedKind"/> mirrors the side of the ledger
/// the server validates that mapping against, so the picker explains what a valid choice is instead of letting an
/// operator guess at an account code and learn from a refusal.
/// </summary>
/// <param name="Title">The posting rule the account serves, in the operator's words.</param>
/// <param name="Key">The server's field name, used to report a validation problem against the right slot.</param>
/// <param name="ExpectedKind">The classification the server insists on.</param>
/// <param name="Current">Reads the mapping this slot owns.</param>
/// <param name="WithAccount">Returns the mapping with this slot pointed at another account.</param>
/// <param name="Why">What postings use it, so the choice is made with the consequence in view.</param>
public sealed record ControlAccountSlot(
    string Title,
    string Key,
    QtsBilling.Contracts.Accounting.AccountKind ExpectedKind,
    Func<QtsBilling.Contracts.Accounting.BusinessAccountingSettingsDto, Guid> Current,
    Func<QtsBilling.Contracts.Accounting.BusinessAccountingSettingsDto, Guid, QtsBilling.Contracts.Accounting.BusinessAccountingSettingsDto> WithAccount,
    string Why)
{
    /// <summary>True when the account the mapping points at is on the side of the ledger the rule needs.</summary>
    public bool Matches(QtsBilling.Contracts.Accounting.AccountKind actual) => actual == ExpectedKind;
}
