using System;
using Foundation;
using GalaxyBudsClient.Platform;
using Serilog;
using UIKit;

namespace GalaxyBudsClient.iOS.Impl;

public class IosDesktopServices : BaseDesktopServices
{
    /// <summary>
    /// iOS apps can't register themselves for autostart.
    /// </summary>
    public override bool IsAutoStartEnabled { get; set; } = false;

    public override void OpenUri(string uri)
    {
        try
        {
            var url = NSUrl.FromString(uri);
            if (url == null)
            {
                Log.Warning("IosDesktopServices: Cannot open invalid URI {Uri}", uri);
                return;
            }

            UIApplication.SharedApplication.OpenUrl(url, new NSDictionary(), null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IosDesktopServices: Failed to open URI {Uri}", uri);
        }
    }
}
