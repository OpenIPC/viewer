using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views;

public partial class MainView : UserControl
{
    // 700px is the design-system breakpoint between desktop sidebar and
    // mobile bottom-nav. Anything narrower (Android phones, pinned desktop
    // windows) gets the bottom strip.
    private const double WideBreakpoint = 700;

    // Orientation-driven fullscreen is mobile-only: on desktop a window that
    // is wider than tall is just a window, not a rotated device.
    private static readonly bool IsMobilePlatform =
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public static readonly DirectProperty<MainView, bool> ShowSidebarProperty =
        AvaloniaProperty.RegisterDirect<MainView, bool>(
            nameof(ShowSidebar), o => o.ShowSidebar);

    public static readonly DirectProperty<MainView, bool> ShowBottomNavProperty =
        AvaloniaProperty.RegisterDirect<MainView, bool>(
            nameof(ShowBottomNav), o => o.ShowBottomNav);

    // Content inset differs by layout (desktop has more breathing room). Exposed
    // as a property because the single shared ContentControl can no longer pick
    // its Padding from two separate layout literals.
    public static readonly DirectProperty<MainView, Thickness> ContentPaddingProperty =
        AvaloniaProperty.RegisterDirect<MainView, Thickness>(
            nameof(ContentPadding), o => o.ContentPadding);

    private static readonly Thickness WidePadding = new(24);
    private static readonly Thickness NarrowPadding = new(12);

    private bool _isWideLayout = true;
    private bool _isFullscreen;
    private bool _showSidebar = true;
    private bool _showBottomNav;
    private Thickness _contentPadding = WidePadding;
    private MainWindowViewModel? _vm;

    public bool ShowSidebar => _showSidebar;
    public bool ShowBottomNav => _showBottomNav;
    public Thickness ContentPadding => _contentPadding;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Seed off the initial bounds — XAML evaluates IsVisible before
        // the first SizeChanged fires, so without this both layouts could
        // flash for a frame on narrow viewports.
        _isWideLayout = Bounds.Width >= WideBreakpoint;
        UpdateChrome();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _isWideLayout = e.NewSize.Width >= WideBreakpoint;
        // Landscape on a phone = the user rotated the device. The VM decides
        // whether that means fullscreen (only while a camera page is open).
        if (IsMobilePlatform)
            _vm?.SetViewportOrientation(e.NewSize.Width > e.NewSize.Height);
        UpdateChrome();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            // Push current orientation in case the device started in landscape
            // and SizeChanged fired before the DataContext was assigned.
            if (IsMobilePlatform && Bounds.Width > 0)
                _vm.SetViewportOrientation(Bounds.Width > Bounds.Height);
        }
        SetFullscreen(_vm?.IsFullscreen ?? false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsFullscreen) && _vm is not null)
            SetFullscreen(_vm.IsFullscreen);
    }

    private void SetFullscreen(bool on)
    {
        if (_isFullscreen == on)
            return;
        _isFullscreen = on;
        UpdateChrome();

        // System status/navigation bars follow the app chrome on mobile;
        // InsetsManager is null on desktop so this is a no-op there.
        var insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (insets is not null)
            insets.IsSystemBarVisible = !on;
    }

    private void UpdateChrome()
    {
        var showSidebar = _isWideLayout && !_isFullscreen;
        var showBottomNav = !_isWideLayout && !_isFullscreen;
        var padding = _isFullscreen ? new Thickness(0)
            : _isWideLayout ? WidePadding : NarrowPadding;

        SetAndRaise(ShowSidebarProperty, ref _showSidebar, showSidebar);
        SetAndRaise(ShowBottomNavProperty, ref _showBottomNav, showBottomNav);
        SetAndRaise(ContentPaddingProperty, ref _contentPadding, padding);
    }
}
