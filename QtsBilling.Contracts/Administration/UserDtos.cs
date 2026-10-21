namespace QtsBilling.Contracts.Administration;

/// <summary>
/// One account the current business can administer, with the authority it actually holds here. An identity may
/// exist in several businesses; this row describes only this one (specification D04).
/// </summary>
public sealed record AdministrationUserDto(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool PasswordResetRequired,
    bool AllLocations,
    decimal MaxDiscountRate,
    IReadOnlyList<Guid> LocationIds,
    long MembershipVersion,
    DateTimeOffset? LastSeenAtUtc);

/// <summary>A page of administrable accounts.</summary>
public sealed record AdministrationUserPage(
    IReadOnlyList<AdministrationUserDto> Items,
    string? NextCursor,
    int PageSize);

/// <summary>
/// Creates the identity and its membership in the caller's business. No password is chosen here: the
/// recipient sets it themselves while redeeming a one-time ticket.
/// </summary>
public sealed record CreateAdministrationUserRequest(
    string Email,
    string DisplayName,
    string Role,
    bool AllLocations,
    IReadOnlyList<Guid>? LocationIds,
    decimal? MaxDiscountRate,
    string Reason);

/// <summary>The created account plus the single-use ticket, returned once and never stored in clear form.</summary>
public sealed record CreateAdministrationUserResultDto(AdministrationUserDto User, SetupTicketDto Ticket);

/// <summary>Changes the authority a membership carries. The version is the one the operator last read.</summary>
public sealed record UpdateMembershipRequest(
    long ExpectedVersion,
    string Role,
    bool AllLocations,
    IReadOnlyList<Guid>? LocationIds,
    decimal? MaxDiscountRate,
    string Reason);

/// <summary>Takes an account out of service. A reason is mandatory because authority is being withdrawn.</summary>
public sealed record DeactivateUserRequest(long ExpectedVersion, string Reason);

/// <summary>Restores an account. Deactivation is never reversed silently by a re-creation of the row.</summary>
public sealed record ActivateUserRequest(long ExpectedVersion, string Reason);

/// <summary>
/// Issues a setup or recovery ticket. The caller proves they are still themselves with their own current
/// password; that credential is verified and never stored or logged.
/// </summary>
public sealed record IssueSetupTicketRequest(string ReauthenticationPassword, string Purpose, string Reason);

/// <summary>
/// The one-time handoff. <paramref name="Secret"/> appears in this response exactly once; the server keeps
/// only its hash, and it is never written to a log, the audit trail or a telemetry stream.
/// </summary>
public sealed record SetupTicketDto(Guid TicketId, string Secret, string Purpose, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Completes setup. There is deliberately no caller-chosen target user: the ticket itself decides whose
/// password is being established, so a valid ticket cannot be pointed at somebody else.
/// </summary>
public sealed record CompleteSetupTicketRequest(Guid TicketId, string Secret, string NewPassword);

/// <summary>The same acknowledgement for every anonymous reset request, so existence is never revealed.</summary>
public sealed record SetupHandoffAcknowledgementDto(string Status, string Instruction);
