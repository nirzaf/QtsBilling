namespace QtsBilling.Maui.Services;

/// <summary>
/// Stable identity of this installation. The identifier is generated on first start and kept in the
/// application preferences; it is the device id used by the sync protocol and by the server's device
/// authorisation. It is not a secret and never leaves the device except as an operation envelope field.
/// </summary>
public interface IDeviceIdentity
{
    Guid DeviceId { get; }

    string DeviceName { get; }
}

public sealed class DeviceIdentity : IDeviceIdentity
{
    private const string DeviceIdKey = "device.id";
    private const string DeviceNameKey = "device.name";

    public DeviceIdentity()
    {
        DeviceId = LoadOrCreateDeviceId();
        DeviceName = Preferences.Default.Get(DeviceNameKey, Environment.MachineName);
    }

    public Guid DeviceId { get; }

    public string DeviceName { get; }

    private static Guid LoadOrCreateDeviceId()
    {
        var stored = Preferences.Default.Get(DeviceIdKey, string.Empty);

        if (Guid.TryParse(stored, out var deviceId) && deviceId != Guid.Empty)
        {
            return deviceId;
        }

        deviceId = Guid.CreateVersion7();
        Preferences.Default.Set(DeviceIdKey, deviceId.ToString("D"));
        return deviceId;
    }
}
