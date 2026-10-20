namespace QtsBilling.Persistence.Sqlite.Entities;

/// <summary>Outbox lifecycle. Acknowledged/Rejected are terminal; only an operator may prune them.</summary>
public enum OutboxState
{
    Pending = 0,
    Sending = 1,
    Acknowledged = 2,
    Rejected = 3,
}

/// <summary>
/// Durable operation-outbox row. It is written in the same SQLite transaction as the provisional money
/// rows it describes and is never removed by a cache rebuild.
/// </summary>
public sealed class OutboxOperation
{
    public Guid OperationId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid BusinessId { get; set; }

    public Guid LocationId { get; set; }

    public string OperationType { get; set; } = string.Empty;

    public int PayloadVersion { get; set; }

    /// <summary>Canonical JSON payload sent on the wire; the server re-validates every business fact.</summary>
    public string CanonicalPayload { get; set; } = string.Empty;

    /// <summary>Lower-case SHA-256 over the canonical payload, for same-key/different-payload detection.</summary>
    public string Sha256Fingerprint { get; set; } = string.Empty;

    public OutboxState State { get; set; } = OutboxState.Pending;

    public int AttemptCount { get; set; }

    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>Last transient server/network error code, retained for support diagnostics.</summary>
    public string? LastErrorCode { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Persisted server acknowledgement. Replay of the same receipt is a no-op.</summary>
public sealed class InboxReceipt
{
    public Guid OperationId { get; set; }

    /// <summary>Posted, AlreadyPosted, NeedsReview, NotAuthorized, DeviceRevoked, UnsupportedPayloadVersion,
    /// TransientRetry or SameKeyDifferentPayload.</summary>
    public string ServerResult { get; set; } = string.Empty;

    public Guid? ServerSaleId { get; set; }

    public string? PostedNumber { get; set; }

    public long? ServerRevision { get; set; }

    public string? ErrorCode { get; set; }

    public DateTime AppliedAtUtc { get; set; }
}

/// <summary>Down-sync cursor for one business/location. Survives cache rebuilds.</summary>
public sealed class SyncState
{
    public Guid BusinessId { get; set; }

    public Guid LocationId { get; set; }

    public long LastRevision { get; set; }

    public long LastOrdinal { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public int BaselineVersion { get; set; }
}
