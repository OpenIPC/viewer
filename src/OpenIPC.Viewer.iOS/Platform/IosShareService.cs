using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using OpenIPC.Viewer.Core.Platform;
using UIKit;

namespace OpenIPC.Viewer.iOS.Platform;

/// <summary>
/// iOS native share (Phase 14.6) via <see cref="UIActivityViewController"/>,
/// presented from the key window's root controller.
/// </summary>
[SupportedOSPlatform("ios")]
public sealed class IosShareService : IShareService
{
    public bool SupportsSystemShare => true;

    public Task ShareFileAsync(string path, string? mimeType, CancellationToken ct)
    {
        var url = NSUrl.FromFilename(path);
        var activity = new UIActivityViewController(new NSObject[] { url }, null);

        var root = KeyRootController();
        if (root is null)
            return Task.CompletedTask;

        // iPad presents activity sheets as a popover anchored to a source rect.
        if (activity.PopoverPresentationController is { } popover && root.View is { } view)
        {
            popover.SourceView = view;
            popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 0, 0);
            popover.PermittedArrowDirections = 0;
        }

        root.PresentViewController(activity, animated: true, completionHandler: null);
        return Task.CompletedTask;
    }

    public Task RevealInFolderAsync(string path) => Task.CompletedTask;

    private static UIViewController? KeyRootController()
    {
        // The project targets iOS 16+, so the scene API is always present; the
        // guard is what teaches the platform-compatibility analyzer that.
        if (!OperatingSystem.IsIOSVersionAtLeast(13))
            return null;

        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is UIWindowScene windowScene)
            {
                foreach (var window in windowScene.Windows)
                {
                    if (window.IsKeyWindow && window.RootViewController is { } vc)
                        return Topmost(vc);
                }
            }
        }
        return null;
    }

    private static UIViewController Topmost(UIViewController vc) =>
        vc.PresentedViewController is { } presented ? Topmost(presented) : vc;
}
