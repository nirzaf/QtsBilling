namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Security-relevant audit trail. Written inside the same transaction as the business change where
/// possible; payloads must never contain secrets, PAN/CVV or passwords (spec §5.3/§5.5).
/// </summary>
public sealed class AuditEvent : Entity
{
    public Guid? BusinessId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? DeviceId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid? EntityId { get; set; }

    public string Action { get; set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Bounded, non-sensitive summary (jsonb).</summary>
    public string? Summary { get; set; }
}

/// <summary>
/// Idempotency ledger for offline sync operations. The unique key
/// <c>(business, device, operation)</c> plus the payload fingerprint guarantees that retries return the
/// original committed outcome instead of posting a second business effect (spec §4.4, QB-20).
/// </summary>
public sealed class ProcessedOperation : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid OperationId { get; set; }

    public string OperationType { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public SyncOutcome Outcome { get; set; }

    public string? ErrorCode { get; set; }

    public Guid? ServerSaleId { get; set; }

    public long? ServerRevision { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; }
}

/// <summary>
/// Per-business monotonic revision counter. Allocation serializes on the row lock
/// (<c>SELECT ... FOR UPDATE</c>) so revisions follow commit order (spec §4.5).
/// </summary>
public sealed class SyncRevision
{
    public Guid BusinessId { get; set; }

    public long Revision { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Commit-ordered change feed entry. Primary key is <c>(business, revision, ordinal)</c>; ordinal
/// orders the multiple changes committed inside one revision (spec §4.5).
/// </summary>
public sealed class SyncChange
{
    public Guid BusinessId { get; set; }

    public long Revision { get; set; }

    public int Ordinal { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public SyncChangeKind Kind { get; set; }

    /// <summary>Projection payload for the entity (jsonb); null for tombstones.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }
}

/// <summary>
/// Mapping from a legacy Mongo identity to the new PostgreSQL identity. Required for idempotent,
/// auditable migration and for resolving any remaining external references (QB-23/QB-24).
/// </summary>
public sealed class LegacyIdentifierMap : Entity
{
    public Guid? BusinessId { get; set; }

    /// <summary>Legacy Mongo collection name (e.g. <c>Customers</c>).</summary>
    public string SourceCollection { get; set; } = string.Empty;

    /// <summary>Legacy ObjectId (24-hex string) or other legacy key.</summary>
    public string LegacyId { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public Guid TargetId { get; set; }

    public DateTimeOffset MigratedAtUtc { get; set; }
}
