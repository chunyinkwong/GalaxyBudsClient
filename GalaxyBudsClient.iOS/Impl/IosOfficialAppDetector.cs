using System.Threading.Tasks;
using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.iOS.Impl;

/// <summary>
/// iOS apps are sandboxed and can't enumerate installed applications. The official Samsung
/// companion apps are not published for iOS anyway, so this always reports "not installed".
/// </summary>
public class IosOfficialAppDetector : IOfficialAppDetector
{
    public Task<bool> IsInstalledAsync()
    {
        return Task.FromResult(false);
    }
}
