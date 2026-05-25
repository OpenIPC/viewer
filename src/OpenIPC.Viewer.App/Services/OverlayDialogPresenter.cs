using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenIPC.Viewer.App.Services;

// Mobile dialog hosting via TopLevel.OverlayLayer. On Android/iOS Avalonia
// runs SingleView lifetime — there's no Window for ShowDialog to target, so
// the desktop `Window.ShowDialog(owner)` path silently no-ops (ResolveOwner
// returns null). This presenter adds a dim background + the dialog Content
// to the active TopLevel's OverlayLayer and awaits a caller-provided
// TaskCompletionSource. Result is delivered when the content closes itself
// via its TCS.
//
// Designed to share the SAME content UserControl as the desktop Window
// wrapper — each dialog moves its inner Grid/StackPanel into a *Content UC
// that owns the TCS; the Window wrapper just bridges TCS → Window.Close.
public static class OverlayDialogPresenter
{
    public static async Task<TResult> ShowAsync<TResult>(Control content, Task<TResult> completion)
    {
        var overlay = GetOverlayLayer();
        if (overlay is null) return default!;

        var dim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = content,
            },
        };

        overlay.Children.Add(dim);
        try
        {
            return await completion.ConfigureAwait(true);
        }
        finally
        {
            overlay.Children.Remove(dim);
        }
    }

    public static bool IsMobile =>
        Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime;

    private static OverlayLayer? GetOverlayLayer()
    {
        Control? root = null;
        var lifetime = Application.Current?.ApplicationLifetime;
        if (lifetime is ISingleViewApplicationLifetime sv)
            root = sv.MainView;
        else if (lifetime is IClassicDesktopStyleApplicationLifetime desk)
            root = desk.MainWindow;
        if (root is null) return null;
        var top = TopLevel.GetTopLevel(root);
        return top is null ? null : OverlayLayer.GetOverlayLayer(top);
    }
}
