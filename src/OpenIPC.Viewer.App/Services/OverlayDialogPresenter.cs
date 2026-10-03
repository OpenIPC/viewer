using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Reactive;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Services;

// Mobile dialog hosting via TopLevel.OverlayLayer. On Android/iOS Avalonia
// runs SingleView lifetime — there's no Window for ShowDialog to target, so
// the desktop `Window.ShowDialog(owner)` path silently no-ops (ResolveOwner
// returns null). This presenter adds a dim background + a bottom-sheet card
// holding the dialog Content to the active TopLevel's OverlayLayer and awaits
// a caller-provided TaskCompletionSource. Result is delivered when the content
// closes itself via its TCS, or when the dialog is dismissed (system Back).
//
// Layout is a bottom sheet: the card stretches full width and pins to the
// bottom edge with rounded top corners, the native mobile pattern. Its height
// is capped to the overlay viewport (minus a top peek of the dim layer) so the
// inner ScrollViewer scrolls long forms instead of overflowing off-screen top.
//
// The card frame (CornerRadius/BoxShadow/ClipToBounds) lives here so each
// *Content UserControl can stay layout-only — content controls supply their
// own background via Bg1Brush, the wrapper provides the modal affordances.
// Designed to share the SAME content UserControl as the desktop Window
// wrapper — each dialog moves its inner Grid/StackPanel into a *Content UC
// that owns the TCS; the Window wrapper just bridges TCS to Window.Close.
public static class OverlayDialogPresenter
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(180);

    // Sliver of the dim layer left visible above the sheet so it reads as a
    // sheet sitting over the page rather than a full-screen takeover. Also
    // keeps the card's top edge clear of the status bar / notch.
    private const double TopPeek = 56;

    // Ceiling on the safe-area inset we pad the sheet by. System bars are ~24-48dp; a larger value
    // means the platform folded the soft keyboard into the inset, and it would be counted twice —
    // SoftKeyboardInset adds the keyboard's own height on top of this.
    private const double MaxSafeInset = 80;

    // Air between a sheet's last row and the navigation bar / gesture pill.
    private const double SheetBottomGap = 14;

    // Dialogs currently on screen, oldest first. Mobile dialogs live in the
    // TopLevel.OverlayLayer; the dim Border does not reliably intercept taps on
    // the bottom nav, so the shell gates navigation on this instead. Desktop
    // uses real modal Windows (ShowDialog) and never goes through here.
    private static readonly List<OpenDialog> Open = new();

    /// <summary>True while at least one overlay (mobile modal) dialog is open.</summary>
    public static bool IsAnyOpen => Open.Count > 0;

    /// <summary>Raised on the UI thread whenever <see cref="IsAnyOpen"/> may have changed.</summary>
    public static event Action? ActiveChanged;

    /// <summary>
    /// System Back over the overlays: the topmost dialog first steps up its own
    /// levels (<see cref="IBackNavigable"/>, e.g. the file manager's folders),
    /// then is cancelled as if the user had hit Cancel. False when none is open.
    /// The caller is parked on the dialog's TaskCompletionSource, so tearing the
    /// sheet down any other way would leave every command that awaits a dialog
    /// (Add camera, Discover, …) disabled for good.
    /// </summary>
    public static bool TryDismissTopmost()
    {
        if (Open.Count == 0)
            return false;
        var top = Open[^1];
        var inner = top.Content as IBackNavigable ?? top.Content.DataContext as IBackNavigable;
        if (inner?.TryGoBack() != true)
            top.Dismissed.TrySetResult();
        return true;
    }

    private sealed record OpenDialog(Control Content, TaskCompletionSource Dismissed);

    // fullScreen → fill the whole TopLevel (no bottom-sheet card / scroll wrapper).
    // Used for the SSH terminal and file manager, which are full-screen pages on
    // mobile, not sheets (phase-13 §13.3).
    public static async Task<TResult> ShowAsync<TResult>(Control content, Task<TResult> completion, bool fullScreen = false)
    {
        var overlay = GetOverlayLayer();
        if (overlay is null) return default!;

        // OverlayLayer is a Canvas: it positions children at (0,0) at their
        // DESIRED size and does NOT honor Stretch/alignment. So a dim Border
        // with HorizontalAlignment.Stretch only covers its own content, not the
        // screen — leaving the dialog floating top-left over an un-dimmed page.
        // We size the dim explicitly to the TopLevel client area instead, and
        // track it so rotation / soft-keyboard insets keep it full-bleed.
        var top = TopLevel.GetTopLevel(overlay);

        var card = new Border
        {
            // Full-width sheet pinned to the bottom edge (sheet mode) or a
            // full-bleed page (fullScreen). Content controls cap with MaxWidth so
            // on wide (tablet/landscape) viewports the form stays a readable
            // column centered inside the stretched sheet.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = fullScreen ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
            // Rounded only at the top for a sheet; square for a full-screen page.
            CornerRadius = fullScreen ? new CornerRadius(0) : new CornerRadius(18, 18, 0, 0),
            // Solid background so it stays opaque edge-to-edge even when the
            // inner content caps its width; content controls also paint Bg1.
            Background = ResolveBrush("Bg1Brush"),
            ClipToBounds = true,
            BoxShadow = fullScreen
                ? default
                : new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = -6,
                    Blur = 40,
                    Spread = 0,
                    Color = Color.FromArgb(0xA0, 0, 0, 0),
                }),
            // Sheet rises from the bottom; full-screen just fades with the dim.
            RenderTransform = fullScreen ? null : TransformOperations.Parse("translateY(24px)"),
            Transitions = fullScreen ? null : new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = FadeIn,
                    Easing = new CubicEaseOut(),
                },
            },
            // Sheet wraps content in a ScrollViewer so tall forms scroll inside
            // the capped card. Full-screen pages (terminal/file manager) manage
            // their own layout and must fill the viewport, so no wrapper.
            Child = fullScreen
                ? content
                : new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = content,
                },
        };

        // Keep whatever the user is typing into visible: the card's bottom padding takes the
        // keyboard's height, and a field below the fold (the login/password pair at the bottom of
        // the add-camera form) has to be scrolled back up inside it. Nudge the focused control into
        // view when focus moves, when the viewport changes, and when the keyboard opens.
        void ScrollFocusedIntoView() => Dispatcher.UIThread.Post(
            () => (top?.FocusManager?.GetFocusedElement() as Control)?.BringIntoView(),
            DispatcherPriority.Background);

        // The overlay layer sits ABOVE the shell, so it does not inherit the
        // safe-area padding MainView applies to itself — without this the sheet's
        // action row (Cancel / Add selected) rendered underneath the Android
        // navigation bar. Full-screen pages also need the status-bar inset.
        var insets = top?.InsetsManager;

        void ApplySafeArea()
        {
            var pad = insets?.SafeAreaPadding ?? default;
            var left = Clamp(pad.Left);
            var right = Clamp(pad.Right);
            // The keyboard is not a system bar and is not subject to MaxSafeInset — it is ~300dp of
            // screen, and under edge-to-edge it can land on top of the card rather than resize the
            // window out from under it. A full-screen page (the SSH terminal) shows it worst: the
            // prompt ends up behind the keys being typed on. Max rather than sum — the keyboard
            // covers the navigation bar, so the bar's inset is already part of it.
            var bottom = Math.Max(Clamp(pad.Bottom) + (fullScreen ? 0 : SheetBottomGap), SoftKeyboardInset.Of(top));
            card.Padding = fullScreen
                ? new Thickness(left, pad.Top > 0 ? Clamp(pad.Top) : 28, right, bottom)
                : new Thickness(left, 0, right, bottom);
        }

        void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => ApplySafeArea();

        ApplySafeArea();
        if (insets is not null)
            insets.SafeAreaChanged += OnSafeAreaChanged;

        var keyboardSub = SoftKeyboardInset.Subscribe(top, () =>
        {
            ApplySafeArea();
            ScrollFocusedIntoView();
        });

        var dim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            // Sized explicitly below (the Canvas won't stretch it for us).
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

        card.AddHandler(InputElement.GotFocusEvent, (object? _, RoutedEventArgs _) => ScrollFocusedIntoView(),
            RoutingStrategies.Bubble);

        // Drive the dim to cover the full client area and cap the sheet to that
        // height (minus the top peek) so long forms scroll inside the card
        // instead of pushing their title off-screen.
        void ApplySize(Size s)
        {
            if (s.Width <= 0 || s.Height <= 0) return;
            dim.Width = s.Width;
            dim.Height = s.Height;
            // Rotation arrives here and nowhere else, and it changes what the keyboard is covering.
            ApplySafeArea();
            // Sheet leaves a top peek of the page; a full-screen page fills all.
            if (!fullScreen)
                card.MaxHeight = Math.Max(0, s.Height - TopPeek);
            ScrollFocusedIntoView();
        }

        IDisposable? sizeSub = null;
        if (top is not null)
        {
            ApplySize(top.ClientSize);
            sizeSub = top.GetObservable(TopLevel.ClientSizeProperty).Subscribe(ApplySize);
        }

        var entry = new OpenDialog(content, new TaskCompletionSource());
        overlay.Children.Add(dim);
        Open.Add(entry);
        ActiveChanged?.Invoke();
        // Kick the transitions after the first layout pass — set synchronously
        // the Avalonia renderer treats them as initial state and skips the
        // animation.
        Dispatcher.UIThread.Post(() =>
        {
            dim.Opacity = 1;
            if (!fullScreen)
                card.RenderTransform = TransformOperations.Parse("translateY(0)");
        }, DispatcherPriority.Background);

        try
        {
            var finished = await Task.WhenAny(completion, entry.Dismissed.Task).ConfigureAwait(true);
            // Dismissed (Back) delivers the same "no result" the Cancel button does.
            return ReferenceEquals(finished, completion)
                ? await completion.ConfigureAwait(true)
                : default!;
        }
        finally
        {
            sizeSub?.Dispose();
            keyboardSub.Dispose();
            if (insets is not null)
                insets.SafeAreaChanged -= OnSafeAreaChanged;
            overlay.Children.Remove(dim);
            Open.Remove(entry);
            ActiveChanged?.Invoke();
        }
    }

    private static double Clamp(double inset) => Math.Clamp(inset, 0, MaxSafeInset);


    private static IBrush? ResolveBrush(string key) =>
        Application.Current is not null
        && Application.Current.TryFindResource(key, out var value)
        && value is IBrush brush
            ? brush
            : null;

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
