using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenIPC.Viewer.App.Controls;

// Thumbnail of the whole frame with the zoomed-in part outlined, so the user
// knows where they are at 6×. Press or drag on it to move the view there.
// Width follows the host (a fifth of it, within limits) so it stays small on a
// phone preview and readable on a desktop window.
public sealed class ZoomMinimap : Control
{
    public static readonly StyledProperty<ZoomPanHost?> HostProperty =
        AvaloniaProperty.Register<ZoomMinimap, ZoomPanHost?>(nameof(Host));

    public static readonly StyledProperty<RtspVideoView?> VideoProperty =
        AvaloniaProperty.Register<ZoomMinimap, RtspVideoView?>(nameof(Video));

    private const double MinThumbWidth = 96;
    private const double MaxThumbWidth = 200;
    private const double HostFraction = 0.2;

    // The thumbnail repaints a few times a second, not per frame: downscaling
    // a 4K frame at full frame rate for a 160px preview is wasted work.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(250);

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
    private static readonly IBrush ShadeBrush = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen ViewportPen = new Pen(Brushes.White, 1.5);

    private DateTime _lastFrameRepaint;

    static ZoomMinimap()
    {
        HostProperty.Changed.AddClassHandler<ZoomMinimap>((m, e) => m.OnHostChanged(
            e.OldValue as ZoomPanHost, e.NewValue as ZoomPanHost));
        VideoProperty.Changed.AddClassHandler<ZoomMinimap>((m, e) => m.OnVideoChanged(
            e.OldValue as RtspVideoView, e.NewValue as RtspVideoView));
    }

    public ZoomPanHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public RtspVideoView? Video { get => GetValue(VideoProperty); set => SetValue(VideoProperty, value); }

    private void OnHostChanged(ZoomPanHost? oldHost, ZoomPanHost? newHost)
    {
        if (oldHost is not null)
        {
            oldHost.ViewportChanged -= OnViewportChanged;
            oldHost.PropertyChanged -= OnHostPropertyChanged;
        }
        if (newHost is not null)
        {
            newHost.ViewportChanged += OnViewportChanged;
            newHost.PropertyChanged += OnHostPropertyChanged;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnVideoChanged(RtspVideoView? oldVideo, RtspVideoView? newVideo)
    {
        if (oldVideo is not null) oldVideo.FrameRendered -= OnFrameRendered;
        if (newVideo is not null) newVideo.FrameRendered += OnFrameRendered;
        InvalidateVisual();
    }

    private void OnViewportChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == ZoomPanHost.ContentAspectProperty)
            InvalidateMeasure();
    }

    private void OnFrameRendered(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible) return;
        var now = DateTime.UtcNow;
        if (now - _lastFrameRepaint < FrameInterval) return;
        _lastFrameRepaint = now;
        InvalidateVisual();
    }

    private double Aspect => Host is { ContentAspect: > 0 } h ? h.ContentAspect : 16.0 / 9.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var hostWidth = Host?.Bounds.Width ?? 0;
        var w = Math.Clamp(hostWidth * HostFraction, MinThumbWidth, MaxThumbWidth);
        if (!double.IsInfinity(availableSize.Width)) w = Math.Min(w, availableSize.Width);
        return new Size(w, Math.Round(w / Aspect));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        context.DrawRectangle(BackgroundBrush, null, bounds, 3, 3);

        if (Video?.CurrentFrame is { } frame)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
                context.DrawImage(frame, new Rect(frame.Size), bounds);
        }

        if (Host is { } host)
        {
            var (x, y, w, h) = host.VisibleContent;
            var view = new Rect(x * bounds.Width, y * bounds.Height, w * bounds.Width, h * bounds.Height);

            // Dim what's off screen so the outlined window reads at a glance.
            context.DrawRectangle(ShadeBrush, null, new Rect(0, 0, bounds.Width, view.Top));
            context.DrawRectangle(ShadeBrush, null, new Rect(0, view.Bottom, bounds.Width, bounds.Height - view.Bottom));
            context.DrawRectangle(ShadeBrush, null, new Rect(0, view.Top, view.Left, view.Height));
            context.DrawRectangle(ShadeBrush, null, new Rect(view.Right, view.Top, bounds.Width - view.Right, view.Height));
            context.DrawRectangle(null, ViewportPen, view);
        }

        context.DrawRectangle(null, FramePen, bounds, 3, 3);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        MoveViewTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        MoveViewTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void MoveViewTo(Point p)
    {
        if (Host is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        Host.CenterOn(p.X / Bounds.Width, p.Y / Bounds.Height);
    }
}
