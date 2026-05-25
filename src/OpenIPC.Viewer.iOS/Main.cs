using UIKit;

namespace OpenIPC.Viewer.iOS;

public static class Application
{
    // iOS app entry — UIApplication.Main spawns the runloop and routes
    // lifecycle callbacks into the named AppDelegate type.
    public static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
