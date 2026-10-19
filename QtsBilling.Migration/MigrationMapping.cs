using System.Text.Json;
using System.Text.Json.Serialization;

namespace QtsBilling.Migration;

/// <summary>
/// The mapping document. It is **data**, not code: the legacy field names live here, so the mapping can be
/// reviewed and corrected against the real inventory without touching the tool (spec §6.1, step 5).
/// </summary>
public sealed record MigrationMapping
{
    /// <summary>Identifies the source; it is part of the identifier-map key.</summary>
    public string SourceEnvironment { get; init; } = "legacy-mongo";

    /// <summary>Bumped whenever the transformation changes; it is part of the identifier-map key.</summary>
    public int TransformationVersion { get; init; } = 1;

    public IReadOnlyList<CollectionMapping> Collections { get; init; } = [];
}

/// <summary>How one legacy collection maps to one target table.</summary>
public sealed record CollectionMapping
{
    public string Collection { get; init; } = string.Empty;

    public string TargetTable { get; init; } = string.Empty;

    /// <summary>The source field that carries the record identity (usually <c>_id</c>).</summary>
    public string SourceKey { get; init; } = "_id";

    /// <summary>Fields that are never exported (card security values, raw credentials).</summary>
    public IReadOnlyList<string> RedactedFields { get; init; } = [];

    public IReadOnlyList<FieldMapping> Fields { get; init; } = [];
}

/// <summary>One target column and where its value comes from.</summary>
public sealed record FieldMapping
{
    /// <summary>The target column.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>The source field (a dotted path for a nested value).</summary>
    public string Source { get; init; } = string.Empty;

    public FieldKind Kind { get; init; } = FieldKind.String;

    /// <summary>The value used when the source field is missing (null means "quarantine the record").</summary>
    public string? Default { get; init; }

    /// <summary>For a reference: the collection the id points at.</summary>
    public string? References { get; init; }

    /// <summary>For a reference: the target table the referenced id belongs to.</summary>
    public string? ReferenceTarget { get; init; }
}

/// <summary>The supported value conversions. Money and quantities are decimal - never double.</summary>
public enum FieldKind
{
    String,
    Int,
    Long,
    Decimal,
    Bool,
    DateTime,
    Guid,
    /// <summary>A source id that is resolved to a stable target id through the identifier map.</summary>
    Reference,
    /// <summary>The source id of the record itself.</summary>
    SourceId,
    /// <summary>
    /// A legacy value that has no counterpart in the new schema (an old customer balance, for example).
    /// It is never imported and never dropped: it becomes a reconciliation exception that needs a decision.
    /// </summary>
    Reconcile,
}
