using QtsBilling.Contracts.Inventory;

namespace QtsBilling.Contracts.Administration;

/// <summary>
/// Document numbering the owner chooses. Only the presentation of a number is configured here; the counter
/// itself stays server-owned, because a client-supplied number would let two documents claim one identity.
/// </summary>
public sealed record NumberingSettingsDto(string InvoicePrefix, int InvoicePadding);

/// <summary>
/// Business identity and setup as one operator-facing view. Financial-account mappings are deliberately
/// absent: <c>/api/v1/accounting/settings</c> remains the single authority for control accounts, and
/// settlement scale is read through it rather than copied into a second mutable place (specification D13).
/// </summary>
public sealed record BusinessSettingsDto(
    Guid BusinessId,
    string Name,
    string? LegalName,
    string TimeZone,
    string BaseCurrency,
    int FiscalYearStartMonth,
    long Version,
    int? SettlementScale,
    NumberingSettingsDto? Numbering,
    IReadOnlyList<BusinessLocationDto> Locations,
    BusinessSetupCompletenessDto Setup);

/// <summary>
/// Whether this business can trade yet, with the precise reason for every missing piece, so a screen can
/// guide the owner instead of failing on the first posting (specification task 06, recipe step 4).
/// <paramref name="HasPostedFacts"/> is the server's own answer to "does history already depend on this shape?":
/// the setup screen shows currency, settlement scale and the fiscal boundary as fixed only when it is true,
/// rather than inviting an attempt the save would refuse (task 14, recipe step 1).
/// </summary>
public sealed record BusinessSetupCompletenessDto(
    bool IsReadyToPost,
    IReadOnlyList<string> BlockingReasons,
    bool HasPostedFacts = false);

/// <summary>
/// Saves the business identity. <paramref name="ExpectedVersion"/> is mandatory: two owners editing setup
/// at once must not silently overwrite each other. Currency and settlement scale may be proposed only while
/// the business has no posted facts; once it has, they are refused rather than repriced underneath history.
/// </summary>
public sealed record SaveBusinessSettingsRequest(
    long ExpectedVersion,
    string Name,
    string? LegalName,
    string TimeZone,
    int FiscalYearStartMonth,
    string? BaseCurrency = null,
    int? SettlementScale = null,
    NumberingSettingsDto? Numbering = null);

/// <summary>A new branch of this business.</summary>
public sealed record CreateLocationRequest(string Name, string? Code, string? Address, string? TimeZone);

/// <summary>Renames or re-points a branch. The version is the one the operator last read.</summary>
public sealed record UpdateLocationRequest(
    long ExpectedVersion,
    string Name,
    string? Code,
    string? Address,
    string? TimeZone);

/// <summary>Takes a branch out of service. A location is never deleted, because posted rows point at it.</summary>
public sealed record DeactivateLocationRequest(long ExpectedVersion, string Reason);
