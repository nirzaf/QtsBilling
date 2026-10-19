namespace QtsBilling.Persistence.Sqlite.Entities;

/// <summary>
/// The enrolled device identity. This table is durable: it is never removed by a cache rebuild and must
/// survive an invalid schema upgrade. One row per installed application/device (QB-17).
/// </summary>
public sealed class DeviceProfile
{
    public Guid DeviceId { get; set; }

    public Guid BusinessId { get; set; }

    public Guid LocationId { get; set; }

    public DateTime EnrolledAtUtc { get; set; }

    /// <summary>Schema version of the local store at the time the device was enrolled/updated.</summary>
    public int SchemaVersion { get; set; }
}
