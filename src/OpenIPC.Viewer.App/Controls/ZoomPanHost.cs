using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Controls;

// Digital zoom + pan for the live video. Children fill the host and share one
// render transform built from DigitalZoomViewport; the host owns the input:
//   - wheel (or Ctrl+wheel when WheelZoomRequiresModifier) zooms at the cursor
//   - pinch zooms around the pinch midpoint
//   - drag pans while zoomed
//   - Shift+drag, or any drag while IsRegionSelectArmed, draws a marquee and
//     zooms to fit it
//   - double-tap zooms in at the tap, or back out when already zoomed
// Overlays that must not scale with the picture (detection boxes, badges) live
// outside the host and map through ViewTransform themselves.
public sealed class ZoomPanHost : Panel
{
    public static readonly StyledProperty<double> ContentAspectProperty =
        AvaloniaProperty.Register<ZoomPanHost, double>(nameof(ContentAspect));

    // Decoded frame width; lets the host tell upscaling (zoomed past 1:1) from
    // downscaling and pick the bitmap filter accordingly.
    public static readonly StyledProperty<int> SourcePixelWidthProperty =
        AvaloniaProperty.Register<ZoomPanHost, int>(nameof(SourcePixelWidth));

    public static readonly StyledProperty<double> MaxZoomProperty =
        AvaloniaProperty.Register<ZoomPanHost, double>(nameof(MaxZoom), DigitalZoomViewport.DefaultMaxZoom);

    public static readonly StyledProperty<bool> IsRegionSelectArmedProperty =
        AvaloniaProperty.Register<ZoomPanHost, bool>(nameof(IsRegionSelectArmed),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    // On a scrolling page a bare wheel should scroll, not zoom — set this there.
    public static readonly StyledProperty<bool> WheelZoomRequiresModifierProperty =
        AvaloniaProperty.Register<ZoomPanHost, bool>(nameof(WheelZoomRequiresModifier));

    public static readonly DirectProperty<ZoomPanHost, double> ZoomProperty =
        AvaloniaProperty.RegisterDirect<ZoomPanHost, double>(nameof(Zoom), o => o.Zoom);

    public static readonly DirectProperty<ZoomPanHost, bool> IsZoomedProperty =
        AvaloniaProperty.RegisterDirect<ZoomPanHost, bool>(nameof(IsZoomed), o => o.IsZoomed);

    public static readonly DirectProperty<ZoomPanHost, Matrix> ViewTransformProperty =
        AvaloniaProperty.RegisterDirect<ZoomPanHost, Matrix>(nameof(ViewTransform), o => o.ViewTransform);

    // Double-tap target zoom, and the step for the +/− buttons.
    private const double DoubleTapZoom = 2.5;
    private const double ButtonZoomFactor = 1.5;
    private const double WheelZoomBase = 1.2;

    // A drag shorter than this is a tap, not a pan or a selection.
    private const double DragThresholdPx = 6;
    private const double MinSelectionPx = 12;

    private enum DragMode { None, Pending, Pan, Select }

    private readonly DigitalZoomViewport _viewport = new();
    private readonly MatrixTransform _transform = new();
    private readonly Border _marquee;
    private readonly HashSet<IPointer> _touches = new();
    private Rect _marqueeRect;
    private Cursor? _crossCursor;
    private Cursor? _panCursor;

    private double _zoom = DigitalZoomViewport.MinZoom;
    private bool _isZoomed;
    private Matrix _viewTransform = Matrix.Identity;

    private DragMode _dragMode;
    private bool _dragSelects;
    private Point _dragStart;
    private Point _dragLast;
    private double _lastPinchScale = 1.0;

    public ZoomPanHost()
    {
        ClipToBounds = true;
        // A transparent background makes the whole area hit-testable, so a pan
        // can start on the letterbox bars too.
        Background = Brushes.Transparent;

        _marquee = new Border
        {
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.Parse("#5B6CD9")),
            Background = new SolidColorBrush(Color.Parse("#335B6CD9")),
            IsHitTestVisible = false,
            IsVisible = false,
            ZIndex = int.MaxValue,
        };
        VisualChildren.Add(_marquee);

        GestureRecognizers.Add(new PinchGestureRecognizer());
        Pinch += OnPinch;
        PinchEnded += OnPinchEnded;
        DoubleTapped += OnDoubleTapped;
    }

    static ZoomPanHost()
    {
        ContentAspectProperty.Changed.AddClassHandler<ZoomPanHost>((h, _) =>
        {
            h._viewport.SetContentAspect(h.ContentAspect);
            h.OnViewportChanged();
        });
        MaxZoomProperty.Changed.AddClassHandler<ZoomPanHost>((h, _) =>
        {
            h._viewport.MaxZoom = h.MaxZoom;
            h.OnViewportChanged();
        });
        SourcePixelWidthProperty.Changed.AddClassHandler<ZoomPanHost>((h, _) => h.UpdateInterpolation());
        IsRegionSelectArmedProperty.Changed.AddClassHandler<ZoomPanHost>((h, _) => h.UpdateCursor());
    }

    public double ContentAspect { get => GetValue(ContentAspectProperty); set => SetValue(ContentAspectProperty, value); }
    public int SourcePixelWidth { get => GetValue(SourcePixelWidthProperty); set => SetValue(SourcePixelWidthProperty, value); }
    public double MaxZoom { get => GetValue(MaxZoomProperty); set => SetValue(MaxZoomProperty, value); }
    public bool IsRegionSelectArmed { get => GetValue(IsRegionSelectArmedProperty); set => SetValue(IsRegionSelectArmedProperty, value); }
    public bool WheelZoomRequiresModifier { get => GetValue(WheelZoomRequiresModifierProperty); set => SetValue(WheelZoomRequiresModifierProperty, value); }

    public double Zoom { get => _zoom; private set => SetAndRaise(ZoomProperty, ref _zoom, value); }
    public bool IsZoomed { get => _isZoomed; private set => SetAndRaise(IsZoomedProperty, ref _isZoomed, value); }

    // view → screen mapping for overlays drawn outside the host.
    public Matrix ViewTransform { get => _viewTransform; private set => SetAndRaise(ViewTransformProperty, ref _viewTransform, value); }

    // Normalized part of the picture on screen (minimap).
    public (double X, double Y, double Width, double Height) VisibleContent => _viewport.VisibleContent;

    // True while a drag is panning or drawing a marquee — the page uses it to
    // keep its swipe-to-next-camera gesture out of the way.
    public bool IsDragging => _dragMode is DragMode.Pan or DragMode.Select;

    public event EventHandler? ViewportChanged;

    public void Reset()
    {
        _viewport.Reset();
        OnViewportChanged();
    }

    public void ZoomIn() => ZoomAroundCenter(ButtonZoomFactor);

    public void ZoomOut() => ZoomAroundCenter(1 / ButtonZoomFactor);

    public void CenterOn(double normalizedX, double normalizedY)
    {
        _viewport.CenterOn(normalizedX, normalizedY);
        OnViewportChanged();
    }

    private void ZoomAroundCenter(double factor)
    {
        _viewport.ZoomAt(Bounds.Width / 2, Bounds.Height / 2, factor);
        OnViewportChanged();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.RenderTransformOrigin = RelativePoint.TopLeft;
            child.RenderTransform = _transform;
            child.Arrange(new Rect(finalSize));
        }
        // Not in Children, so Panel never lays it out — place it here.
        _marquee.Measure(_marqueeRect.Size);
        _marquee.Arrange(_marqueeRect);

        if (finalSize.Width != _viewport.ViewWidth || finalSize.Height != _viewport.ViewHeight)
        {
            _viewport.SetViewSize(finalSize.Width, finalSize.Height);
            OnViewportChanged();
        }
        return finalSize;
    }

    private void OnViewportChanged()
    {
        var z = _viewport.Zoom;
        var m = new Matrix(z, 0, 0, z, -_viewport.OffsetX * z, -_viewport.OffsetY * z);
        _transform.Matrix = m;
        ViewTransform = m;
        Zoom = z;
        IsZoomed = _viewport.IsZoomed;
        UpdateInterpolation();
        UpdateCursor();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    // Past 1:1 the picture is being upscaled: switch to the high-quality
    // (bicubic) filter so zoomed detail stays smooth instead of blocky. Below
    // 1:1 keep the renderer default — high quality there means rebuilding
    // mipmaps of a 4K/8K frame on every frame, for no visible gain.
    private void UpdateInterpolation()
    {
        var sourceWidth = SourcePixelWidth;
        var contentWidth = _viewport.ContentRect.Width;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var upscaling = sourceWidth > 0 && contentWidth > 0
            && _viewport.Zoom * contentWidth * scaling > sourceWidth * 1.01;

        var mode = upscaling ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.Unspecified;
        if (Avalonia.Media.RenderOptions.GetBitmapInterpolationMode(this) != mode)
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(this, mode);
    }

    private void UpdateCursor()
    {
        Cursor = IsRegionSelectArmed ? _crossCursor ??= new Cursor(StandardCursorType.Cross)
            : IsZoomed ? _panCursor ??= new Cursor(StandardCursorType.SizeAll)
            : null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (WheelZoomRequiresModifier && !ctrl && !IsZoomed) return;

        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;
        var p = e.GetPosition(this);
        _viewport.ZoomAt(p.X, p.Y, Math.Pow(WheelZoomBase, delta));
        OnViewportChanged();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Pointer.Type == PointerType.Touch) _touches.Add(e.Pointer);

        // A second finger means a pinch — drop whatever the first one started.
        if (_touches.Count > 1)
        {
            CancelDrag();
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        _dragSelects = IsRegionSelectArmed || (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (!_dragSelects && !IsZoomed) return;

        // Capture only once the pointer really moves: capturing on press would
        // retarget the release and break tap / double-tap recognition.
        _dragMode = DragMode.Pending;
        _dragStart = _dragLast = e.GetPosition(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragMode == DragMode.None || _touches.Count > 1) return;

        var p = e.GetPosition(this);
        if (_dragMode == DragMode.Pending)
        {
            if (Math.Abs(p.X - _dragStart.X) < DragThresholdPx && Math.Abs(p.Y - _dragStart.Y) < DragThresholdPx)
                return;
            _dragMode = _dragSelects ? DragMode.Select : DragMode.Pan;
            e.Pointer.Capture(this);
        }

        if (_dragMode == DragMode.Pan)
        {
            _viewport.Pan(p.X - _dragLast.X, p.Y - _dragLast.Y);
            OnViewportChanged();
        }
        else
        {
            _marqueeRect = SelectionRect(p);
            _marquee.IsVisible = true;
            InvalidateArrange();
        }
        _dragLast = p;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _touches.Remove(e.Pointer);

        if (_dragMode == DragMode.Select)
        {
            var r = SelectionRect(e.GetPosition(this));
            if (r.Width >= MinSelectionPx && r.Height >= MinSelectionPx)
            {
                _viewport.ZoomToRect(r.X, r.Y, r.Width, r.Height);
                OnViewportChanged();
                // One selection per arming, like a camera app's "zoom to area".
                IsRegionSelectArmed = false;
            }
            e.Handled = true;
        }
        else if (_dragMode == DragMode.Pan)
        {
            e.Handled = true;
        }

        if (_dragMode != DragMode.None)
        {
            CancelDrag();
            if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _touches.Remove(e.Pointer);
        CancelDrag();
    }

    private void CancelDrag()
    {
        _dragMode = DragMode.None;
        if (_marquee.IsVisible)
        {
            _marquee.IsVisible = false;
            InvalidateArrange();
        }
    }

    private Rect SelectionRect(Point p)
    {
        // Clip to the host so a drag that runs off the edge still selects up to it.
        var x0 = Math.Clamp(Math.Min(_dragStart.X, p.X), 0, Bounds.Width);
        var y0 = Math.Clamp(Math.Min(_dragStart.Y, p.Y), 0, Bounds.Height);
        var x1 = Math.Clamp(Math.Max(_dragStart.X, p.X), 0, Bounds.Width);
        var y1 = Math.Clamp(Math.Max(_dragStart.Y, p.Y), 0, Bounds.Height);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsRegionSelectArmed) return;
        if (IsZoomed)
        {
            _viewport.Reset();
        }
        else
        {
            var p = e.GetPosition(this);
            _viewport.SetZoomAt(p.X, p.Y, DoubleTapZoom);
        }
        OnViewportChanged();
        e.Handled = true;
    }

    // PinchEventArgs.Scale is cumulative since the gesture began and
    // ScaleOrigin is the starting midpoint in this control's coordinates —
    // apply the per-event ratio around that point.
    private void OnPinch(object? sender, PinchEventArgs e)
    {
        CancelDrag();
        var factor = _lastPinchScale > 0 ? e.Scale / _lastPinchScale : 1.0;
        _lastPinchScale = e.Scale;
        if (factor > 0 && Math.Abs(factor - 1.0) > 0.0001)
        {
            _viewport.ZoomAt(e.ScaleOrigin.X, e.ScaleOrigin.Y, factor);
            OnViewportChanged();
        }
        e.Handled = true;
    }

    private void OnPinchEnded(object? sender, PinchEndedEventArgs e)
    {
        _lastPinchScale = 1.0;
        _touches.Clear();
        e.Handled = true;
    }
}
