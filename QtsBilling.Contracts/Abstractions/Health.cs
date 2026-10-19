namespace QtsBilling.Contracts.Abstractions;

/// <summary>Liveness/readiness payload for the API host (QB-05).</summary>
public sealed record HealthDto(string Status, string Store, DateTimeOffset CheckedAtUtc);
