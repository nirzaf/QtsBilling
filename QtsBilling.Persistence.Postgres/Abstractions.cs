namespace QtsBilling.Persistence.Postgres;

/// <summary>An entity with a stable surrogate key.</summary>
public interface IEntity
{
    Guid Id { get; set; }
}

/// <summary>Every business-owned row carries an explicit tenant key (specification §3.1/§3.3, F-22).</summary>
public interface ITenantScoped
{
    Guid BusinessId { get; set; }
}

/// <summary>Explicit optimistic-concurrency version, incremented on every update.</summary>
public interface IVersioned
{
    long Version { get; set; }
}

/// <summary>UTC audit timestamps stored as <c>timestamptz</c>.</summary>
public interface ITimestamped
{
    DateTimeOffset CreatedAtUtc { get; set; }

    DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Base entity for immutable/insert-only tables (movements, journal lines, audit).</summary>
public abstract class Entity : IEntity, ITimestamped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Base entity for mutable, tenant-owned aggregate roots and master data.</summary>
public abstract class TenantEntity : Entity, ITenantScoped, IVersioned
{
    public Guid BusinessId { get; set; }

    public long Version { get; set; }
}
