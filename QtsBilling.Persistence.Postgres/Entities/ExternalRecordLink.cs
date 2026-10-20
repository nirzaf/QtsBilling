namespace QtsBilling.Persistence.Postgres.Entities;

/// <summary>
/// Immutable native-onboarding source identity (specification task 19, decision D13/D17). One row binds
/// a complete external identity to exactly one target row and one payload digest. Re-importing the same
/// body reuses the target; a changed body or a different target is a conflict, never an upsert.
/// </summary>
public sealed class ExternalRecordLink : Entity, ITenantScoped
{
    public Guid BusinessId { get; set; }

    /// <summary>The approved import batch that first created this identity.</summary>
    public Guid BatchId { get; set; }

    /// <summary>The trusted dataset namespace; the native format uses <c>native</c>.</summary>
    public string DatasetNamespace { get; set; } = "native";

    public string ResourceType { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    /// <summary>SHA-256 of the exact accepted NDJSON line.</summary>
    public string PayloadSha256 { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public Guid TargetId { get; set; }
}
