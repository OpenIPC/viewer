using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenIPC.Viewer.App.Controls;

// Digital-zoom chrome shared by the live and recording pages: minimap plus a
// −/level/+/reset chip for a ZoomPanHost. Hides itself while not zoomed.
public sealed partial class ZoomOverlay : UserControl
{
    public static readonly StyledProperty<ZoomPanHost?> HostProperty =
        AvaloniaProperty.Register<ZoomOverlay, ZoomPanHost?>(nameof(Host));

    public static readonly StyledProperty<RtspVideoView?> VideoProperty =
        AvaloniaProperty.Register<ZoomOverlay, RtspVideoView?>(nameof(Video));

    public ZoomOverlay()
    {
        InitializeComponent();
    }

    static ZoomOverlay()
    {
        HostProperty.Changed.AddClassHandler<ZoomOverlay>((o, e) =>
            o.OnHostChanged(e.OldValue as ZoomPanHost, e.NewValue as ZoomPanHost));
        VideoProperty.Changed.AddClassHandler<ZoomOverlay>((o, _) => o.Minimap.Video = o.Video);
    }

    public ZoomPanHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public RtspVideoView? Video { get => GetValue(VideoProperty); set => SetValue(VideoProperty, value); }

    private void OnHostChanged(ZoomPanHost? oldHost, ZoomPanHost? newHost)
    {
        if (oldHost is not null) oldHost.ViewportChanged -= OnViewportChanged;
        if (newHost is not null) newHost.ViewportChanged += OnViewportChanged;
        Minimap.Host = newHost;
        Refresh();
    }

    private void OnViewportChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        IsVisible = Host?.IsZoomed ?? false;
        ZoomLabel.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}×", Host?.Zoom ?? 1.0);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Host?.ZoomIn();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Host?.ZoomOut();

    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => Host?.Reset();
}
