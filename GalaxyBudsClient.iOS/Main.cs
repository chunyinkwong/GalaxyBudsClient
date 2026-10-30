using UIKit;

namespace GalaxyBudsClient.iOS;

public class Application
{
    // iOS entry point. UIKit instantiates the registered AppDelegate and runs the main loop.
    private static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
