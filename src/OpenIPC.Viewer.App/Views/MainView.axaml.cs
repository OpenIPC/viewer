using Avalonia;
using Avalonia.Controls;

namespace OpenIPC.Viewer.App.Views;

public partial class MainView : UserControl
{
    // 700px is the design-system breakpoint between desktop sidebar and
    // mobile bottom-nav. Anything narrower (Android phones, pinned desktop
    // windows) gets the bottom strip.
    private const double WideBreakpoint = 700;

    public static readonly DirectProperty<MainView, bool> IsWideLayoutProperty =
        AvaloniaProperty.RegisterDirect<MainView, bool>(
            nameof(IsWideLayout), o => o.IsWideLayout);

    public static readonly DirectProperty<MainView, bool> IsNarrowLayoutProperty =
        AvaloniaProperty.RegisterDirect<MainView, bool>(
            nameof(IsNarrowLayout), o => o.IsNarrowLayout);

    private bool _isWideLayout = true;

    public bool IsWideLayout
    {
        get => _isWideLayout;
        private set
        {
            if (SetAndRaise(IsWideLayoutProperty, ref _isWideLayout, value))
                RaisePropertyChanged(IsNarrowLayoutProperty, !value, value == false);
        }
    }

    public bool IsNarrowLayout => !_isWideLayout;

    public MainView()
    {
        InitializeComponent();
        // Seed off the initial bounds — XAML evaluates IsVisible before
        // the first SizeChanged fires, so without this both layouts could
        // flash for a frame on narrow viewports.
        IsWideLayout = Bounds.Width >= WideBreakpoint;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        IsWideLayout = e.NewSize.Width >= WideBreakpoint;
    }
}
