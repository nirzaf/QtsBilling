namespace QtsBilling.Contracts.Sync;

/// <summary>How a device conflict is resolved by the operator.</summary>
public enum SyncConflictResolution
{
    /// <summary>Take the server value and drop the local one.</summary>
    UseServer = 0,

    /// <summary>Keep the locally captured operation and let the server deal with it.</summary>
    KeepLocal = 1,
}

/// <summary>
/// A change the server published that touches a record a pending local operation also touches. The device
/// never resolves it silently: the operator decides (QB-22).
/// </summary>
public sealed record SyncConflict(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Reason,
    string ServerValue,
    string LocalValue,
    DateTimeOffset DetectedAtUtc);

/// <summary>The device sync status shown to the operator.</summary>
public sealed record SyncStatus(
    int PendingOperations,
    int Conflicts,
    long LastRevision,
    int LastOrdinal,
    DateTimeOffset? LastSuccessUtc,
    bool Online,
    string? LastError);

/// <summary>The result of one push or pull run.</summary>
public sealed record SyncRunReport(
    int Pushed,
    int Pulled,
    int Conflicts,
    int Failed,
    string? Error);

/// <summary>
/// One terminal as the owner administers it: what it is, where it belongs, when it last spoke, and how much
/// it still holds that nobody has reconciled (specification task 08, recipe step 4).
/// <paramref name="Version"/> is part of the projection on purpose: every lifecycle change is versioned, so the
/// operator who can see a terminal must also be able to send the version they looked at - without reading it
/// out of a database tool (requirement R07, decision D06).
/// </summary>
public sealed record DeviceInfoDto(
    Guid DeviceId,
    string Name,
    string Platform,
    string Status,
    Guid? LocationId,
    string? LocationName,
    string? AppVersion,
    int SchemaVersion,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? GrantExpiresAtUtc,
    int ReceivedOperations,
    int OperationsNeedingReview,
    decimal DeviceHeldProvisionalCash,
    long Version);

/// <summary>Withdraws a terminal's authority. Revoked identities are kept, never deleted.</summary>
public sealed record RevokeDeviceRequest(long ExpectedVersion, string Reason);

/// <summary>
/// Points a terminal at another location. The clearance must exist first: reassignment cannot be used to make
/// device-held money or stored operations disappear, and stored operations keep their original scope.
/// </summary>
public sealed record ReassignDeviceRequest(long ExpectedVersion, Guid LocationId, Guid ClearanceId, string Reason);

/// <summary>Retires a terminal for good, after its money and operations were reconciled.</summary>
public sealed record RetireDeviceRequest(long ExpectedVersion, string Reason);

/// <summary>
/// The manager's record of what a terminal still holds. Amount and operation count are mandatory and are
/// compared against what the server can see, so an acknowledgement cannot understate the exposure.
/// </summary>
public sealed record IssueReassignmentClearanceRequest(
    long ExpectedVersion,
    decimal AcknowledgedProvisionalCash,
    int AcknowledgedOperations,
    string Reason);

/// <summary>A clearance that has been granted and not yet spent.</summary>
public sealed record DeviceReassignmentClearanceDto(
    Guid ClearanceId,
    Guid DeviceId,
    decimal AcknowledgedProvisionalCash,
    int AcknowledgedOperations,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Asks for a short enrolment code for one branch. The code is the operator's way of proving that the terminal
/// standing in front of them belongs to this business and this counter; it is hashed on the server, expires, and
/// is spent by the first enrolment that uses it (specification task 08).
/// </summary>
public sealed record CreateEnrolmentChallengeRequest(Guid LocationId);

/// <summary>The code as handed to the operator, with the branch and the moment it stops working.</summary>
public sealed record CreateEnrolmentChallengeResponse(string EnrolmentCode, Guid LocationId, DateTimeOffset ExpiresAtUtc);

/// <summary>The terminal's own enrolment: what it is called, where it runs, and the code it was handed.</summary>
public sealed record EnrolDeviceRequest(
    string Name,
    string Platform,
    Guid LocationId,
    string EnrolmentCode,
    string? AppVersion = null,
    int? SchemaVersion = null);

/// <summary>The identity the terminal keeps from then on.</summary>
public sealed record DeviceEnrolmentResponse(Guid DeviceId, DateTimeOffset EnrolledAtUtc);

/// <summary>The answer a lifecycle command gives: the record's new state and the version to send next time.</summary>
public sealed record DeviceLifecycleResponse(Guid DeviceId, string Status, DateTimeOffset? RevokedAtUtc, long Version);

/// <summary>The verification key this instance publishes to its own enrolled terminals for offline grants.</summary>
public sealed record OfflineGrantKeyResponse(string KeyId, string PublicKey);
