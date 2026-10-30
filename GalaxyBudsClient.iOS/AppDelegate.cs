using System;
using Avalonia;
using Avalonia.iOS;
using GalaxyBudsClient.iOS.Impl;
using GalaxyBudsClient.Platform;
using ReactiveUI.Avalonia;

namespace GalaxyBudsClient.iOS;

/// <summary>
/// Avalonia application delegate for iOS. Mirrors GalaxyBudsClient.Android/MainActivity.cs:
/// it initialises the shared program (logging, crash reporting, settings migration) and injects
/// the iOS platform backend before the Avalonia app is created.
/// </summary>
[Foundation.Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        Program.Startup(false);
        PlatformImpl.InjectExternalBackend(new IosPlatformImplCreator());

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
    }
}
