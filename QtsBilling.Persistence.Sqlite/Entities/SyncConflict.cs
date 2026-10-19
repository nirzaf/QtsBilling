namespace QtsBilling.Persistence.Sqlite.Entities;

/// <summary>
/// A change the server published that touches a record a pending local operation also touches. The device
/// never resolves it silently: the operator decides (QB-22).
/// </summary>
public sealed class SyncConflict
{
    public Guid Id { get; set; }

    public Guid BusinessId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? ServerValue { get; set; }

    public string? LocalValue { get; set; }

    public DateTimeOffset DetectedAtUtc { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public string? Resolution { get; set; }
}
