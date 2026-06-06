using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Services;

// Mobile dialog hosting via TopLevel.OverlayLayer. On Android/iOS Avalonia
// runs SingleView lifetime — there's no Window for ShowDialog to target, so
// the desktop `Window.ShowDialog(owner)` path silently no-ops (ResolveOwner
// returns null). This presenter adds a dim background + a rounded card with
// shadow + the dialog Content to the active TopLevel's OverlayLayer and
// awaits a caller-provided TaskCompletionSource. Result is delivered when
// the content closes itself via its TCS.
//
// The card frame (CornerRadius/BoxShadow/ClipToBounds) lives here so each
// *Content UserControl can stay layout-only — content controls supply their
// own background via Bg1Brush, the wrapper provides the modal affordances.
// Designed to share the SAME content UserControl as the desktop Window
// wrapper — each dialog moves its inner Grid/StackPanel into a *Content UC
// that owns the TCS; the Window wrapper just bridges TCS → Window.Close.
public static class OverlayDialogPresenter
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(160);

    // Number of overlay dialogs currently on screen. Mobile dialogs live in the
    // TopLevel.OverlayLayer; the dim Border does not reliably intercept taps on
    // the bottom nav, so the shell gates navigation on this instead. Desktop
    // uses real modal Windows (ShowDialog) and never goes through here.
    private static int _activeCount;

    /// <summary>True while at least one overlay (mobile modal) dialog is open.</summary>
    public static bool IsAnyOpen => _activeCount > 0;

    /// <summary>Raised on the UI thread whenever <see cref="IsAnyOpen"/> may have changed.</summary>
    public static event Action? ActiveChanged;

    public static async Task<TResult> ShowAsync<TResult>(Control content, Task<TResult> completion)
    {
        var overlay = GetOverlayLayer();
        if (overlay is null) return default!;

        var card = new Border
        {
            // Margin keeps the card off-edge on phone viewports — content
            // controls cap with MaxWidth so they don't try to render at their
            // desktop size on a 360px-wide screen. 20px gives the BoxShadow
            // visible breathing room.
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 12,
                Blur = 40,
                Spread = 0,
                Color = Color.FromArgb(0xA0, 0, 0, 0),
            }),
            // ScrollViewer wraps content so tall dialogs (raw-config editor,
            // long camera-editor on landscape phones) stay reachable instead
            // of overflowing the viewport.
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            },
        };

        var dim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = FadeIn,
                },
            },
            Child = card,
        };

        overlay.Children.Add(dim);
        _activeCount++;
        ActiveChanged?.Invoke();
        // Kick the opacity transition after the first layout pass — set
        // synchronously the Avalonia renderer treats it as initial state
        // and skips the animation.
        Dispatcher.UIThread.Post(() => dim.Opacity = 1, DispatcherPriority.Background);

        try
        {
            return await completion.ConfigureAwait(true);
        }
        finally
        {
            overlay.Children.Remove(dim);
            _activeCount--;
            ActiveChanged?.Invoke();
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
