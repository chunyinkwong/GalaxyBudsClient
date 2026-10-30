using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.iOS.Impl;

/// <summary>
/// Factory for the iOS platform implementations. Hotkeys, media key remotes and the notification
/// listener don't exist on iOS, so those features are disabled by returning null.
/// </summary>
public class IosPlatformImplCreator : IPlatformImplCreator
{
    public IDesktopServices? CreateDesktopServices() => new IosDesktopServices();
    public IBluetoothService? CreateBluetoothService() => new IosBluetoothService();
    public IHotkeyBroadcast? CreateHotkeyBroadcast() => null;
    public IHotkeyReceiver? CreateHotkeyReceiver() => null;
    public IMediaKeyRemote? CreateMediaKeyRemote() => null;
    public INotificationListener? CreateNotificationListener() => null;
    public IOfficialAppDetector? CreateOfficialAppDetector() => new IosOfficialAppDetector();
}
