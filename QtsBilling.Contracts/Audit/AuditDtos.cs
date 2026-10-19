namespace QtsBilling.Contracts.Audit;

/// <summary>
/// One structured audit entry (QB-14): who did what to which entity, when, with a correlation id that
/// ties it to the request that produced it. Summaries are bounded and non-sensitive - credentials and
/// contact details are masked before they are stored.
/// </summary>
public sealed record AuditEventDto(
    Guid Id,
    Guid? BusinessId,
    Guid? UserId,
    Guid? DeviceId,
    string EntityType,
    Guid? EntityId,
    string Action,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    string? Summary);
