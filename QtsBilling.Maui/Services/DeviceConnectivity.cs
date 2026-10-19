namespace QtsBilling.Maui.Services;

/// <summary>Connectivity state of the device, used to keep the app usable while offline.</summary>
public interface IDeviceConnectivity
{
    bool IsOnline { get; }

    string Description { get; }
}

public sealed class DeviceConnectivity : IDeviceConnectivity
{
    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public string Description => Connectivity.Current.NetworkAccess.ToString();
}
