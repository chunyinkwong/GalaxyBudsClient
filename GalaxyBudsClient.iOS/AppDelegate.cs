using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.iOS;
using GalaxyBudsClient.iOS.Impl;
using GalaxyBudsClient.Platform;
using ObjCRuntime;
using ReactiveUI.Avalonia;
using Serilog;

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
        InstallCrashDiagnostics();

        try
        {
            Program.Startup(false);
            PlatformImpl.InjectExternalBackend(new IosPlatformImplCreator());
        }
        catch (Exception ex)
        {
            Report("Program.Startup / platform injection failed", ex);
            throw;
        }

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
    }

    /// <summary>
    /// On iOS a managed exception that escapes into native code (dispatcher callbacks, delegate
    /// trampolines, objective-c targets) is converted into an NSException by the runtime and then
    /// aborts the process. Nothing writes that exception anywhere by default, which makes the crash
    /// report useless: app frames are AOT/stripped and the .ips has no "reason" string.
    /// These hooks funnel the exception (type, message, managed stack) into application.log and
    /// into the device console (pymobiledevice3 syslog / Console.app / 3uTools live log).
    /// </summary>
    private static void InstallCrashDiagnostics()
    {
        Runtime.MarshalManagedException += (_, args) =>
            Report("Managed exception escaped into native code", args.Exception);
        Runtime.MarshalObjectiveCException += (_, args) =>
            Report("Objective-C exception raised in managed code", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report("Unhandled managed exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Report("Unobserved task exception", args.Exception);
    }

    private static void Report(string what, Exception? exception)
    {
        try
        {
            Log.Fatal(exception, "{What}", what);
        }
        catch (Exception)
        {
            // Serilog may not be configured yet; the console output below is the fallback
        }

        // stdout/stderr are forwarded to the device console for the whole process lifetime
        try
        {
            Console.Error.WriteLine($"===== {what} =====");
            Console.Error.WriteLine(exception?.ToString() ?? "(no exception object)");
        }
        catch (Exception)
        {
            // nothing else we can do while crashing
        }
    }
}
