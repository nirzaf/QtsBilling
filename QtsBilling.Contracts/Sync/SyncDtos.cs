using System.Text.Json;

namespace QtsBilling.Contracts.Sync;

/// <summary>
/// Offline operation envelope (specification §4.4). The wire example in the specification is
/// normative for this envelope structure; concrete sale payloads live in
/// <see cref="Sales.SaleCaptureRequest"/> and are versioned through <see cref="PayloadVersion"/>.
/// </summary>
public sealed record SyncOperationEnvelope(
    Guid DeviceId,
    Guid OperationId,
    Guid BusinessId,
    Guid LocationId,
    string Type,
    int PayloadVersion,
    DateTimeOffset CreatedAtUtc,
    string PayloadHash,
    JsonElement Payload);

/// <summary>Kinds of offline-capturable operations accepted by the first release.</summary>
public static class SyncOperationTypes
{
    public const string PosSaleCaptureV1 = "pos.sale.capture.v1";
}

/// <summary>Typed, stable result of processing one sync operation (§4.4).</summary>
public sealed record SyncOperationReceipt(
    Guid OperationId,
    SyncOutcome Outcome,
    string? ErrorCode,
    string? Message,
    Guid? ServerSaleId,
    string? PostedReference,
    long? ServerRevision,
    DateTimeOffset ProcessedAtUtc)
{
    public bool IsTerminal => Outcome is not SyncOutcome.TransientRetry;
}

/// <summary>Exactly one business outcome per operation.</summary>
public enum SyncOutcome
{
    Posted,
    AlreadyPosted,
    NeedsReview,
    NotAuthorized,
    DeviceRevoked,
    UnsupportedPayloadVersion,
    TransientRetry,
    SameKeyDifferentPayload,
}

/// <summary>Change-feed entry (upsert or tombstone) at a committed revision/ordinal.</summary>
public sealed record SyncChangeDto(
    long Revision,
    int Ordinal,
    string EntityType,
    Guid EntityId,
    SyncChangeKind Kind,
    JsonElement? Payload,
    DateTimeOffset ChangedAtUtc);

public enum SyncChangeKind
{
    Upsert,
    Tombstone,
}

/// <summary>
/// One page of the commit-ordered change feed. Ordering is the compound (revision, ordinal) key
/// produced by the per-business locked revision counter (specification §4.5).
/// </summary>
public sealed record SyncChangesPage(
    IReadOnlyList<SyncChangeDto> Changes,
    long NextRevision,
    int NextOrdinal,
    bool HasMore,
    string SchemaVersion,
    bool ResyncRequired)
{
    public const string CurrentSchemaVersion = "1";
}
