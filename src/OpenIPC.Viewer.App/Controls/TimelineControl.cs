using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Timeline;

namespace OpenIPC.Viewer.App.Controls;

// Canvas-rendered archive timeline (Phase 16.4). Draws recording segments,
// motion/detection markers and the playhead on one scale; supports
// cursor-anchored wheel zoom, drag-pan, click-to-seek and marker tooltips.
// Rendered with DrawingContext (not hundreds of visuals) — markers are bucketed
// per pixel column so a day of detections stays cheap to paint.
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<DateTime> TotalStartProperty =
        AvaloniaProperty.Register<TimelineControl, DateTime>(nameof(TotalStart));

    public static readonly StyledProperty<DateTime> TotalEndProperty =
        AvaloniaProperty.Register<TimelineControl, DateTime>(nameof(TotalEnd));

    public static readonly StyledProperty<IReadOnlyList<TimelineSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineControl, IReadOnlyList<TimelineSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IReadOnlyList<TimelineMarker>?> MarkersProperty =
        AvaloniaProperty.Register<TimelineControl, IReadOnlyList<TimelineMarker>?>(nameof(Markers));

    // Playhead position as an absolute time; null hides it.
    public static readonly StyledProperty<DateTime?> PositionProperty =
        AvaloniaProperty.Register<TimelineControl, DateTime?>(nameof(Position));

    // Executed with a DateTime parameter when the user clicks/seeks.
    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<TimelineControl, ICommand?>(nameof(SeekCommand));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(TrackBrush), new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x27)));

    public static readonly StyledProperty<IBrush?> SegmentBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(SegmentBrush), new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x46)));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(PlayheadBrush), new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(LabelBrush), new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA6)));

    private static readonly IBrush MotionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly IBrush DetectionBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x40, 0x40));
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA6));

    private const double TrackTop = 8;
    private const double TrackHeight = 26;
    private const double MarkerHitPx = 6;
    private const double PanThresholdPx = 4;

    private TimelineViewport? _viewport;
    private Point? _pressPoint;
    private bool _dragging;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(
            TotalStartProperty, TotalEndProperty, SegmentsProperty,
            MarkersProperty, PositionProperty, TrackBrushProperty,
            SegmentBrushProperty, PlayheadBrushProperty, LabelBrushProperty);
    }

    public DateTime TotalStart { get => GetValue(TotalStartProperty); set => SetValue(TotalStartProperty, value); }
    public DateTime TotalEnd { get => GetValue(TotalEndProperty); set => SetValue(TotalEndProperty, value); }
    public IReadOnlyList<TimelineSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IReadOnlyList<TimelineMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public DateTime? Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? SegmentBrush { get => GetValue(SegmentBrushProperty); set => SetValue(SegmentBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Rebuild the viewport when the total range changes (new recording).
        if (change.Property == TotalStartProperty || change.Property == TotalEndProperty)
            _viewport = null;
    }

    private TimelineViewport Viewport =>
        _viewport ??= new TimelineViewport(TotalStart, TotalEnd);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0 || TotalEnd <= TotalStart) return;

        var vp = Viewport;

        // Track background.
        context.DrawRectangle(TrackBrush, null, new Rect(0, TrackTop, w, TrackHeight));

        // Recording segments.
        var segs = Segments;
        if (segs is not null)
        {
            foreach (var s in segs)
            {
                var x0 = Math.Max(0, vp.TimeToX(s.Start, w));
                var x1 = Math.Min(w, vp.TimeToX(s.End, w));
                if (x1 <= x0) continue;
                context.DrawRectangle(SegmentBrush, null, new Rect(x0, TrackTop, x1 - x0, TrackHeight));
            }
        }

        DrawMarkers(context, vp, w);
        DrawTicks(context, vp, w, h);
        DrawPlayhead(context, vp, w);
    }

    private void DrawMarkers(DrawingContext context, TimelineViewport vp, double w)
    {
        var markers = Markers;
        if (markers is null || markers.Count == 0) return;

        // Bucket by integer pixel column so a dense day collapses to one tick
        // per column (cheap paint + readable). The bucket's kind is the most
        // "severe" present (Detection > Motion > Other).
        var buckets = new Dictionary<int, TimelineMarkerKind>();
        foreach (var m in markers)
        {
            if (m.Time < vp.VisibleStart || m.Time > vp.VisibleEnd) continue;
            var col = (int)Math.Round(vp.TimeToX(m.Time, w));
            if (col < 0 || col > w) continue;
            if (buckets.TryGetValue(col, out var existing))
                buckets[col] = Severer(existing, m.Kind);
            else
                buckets[col] = m.Kind;
        }

        foreach (var (col, kind) in buckets)
        {
            var brush = BrushFor(kind);
            context.DrawRectangle(brush, null, new Rect(col - 1, TrackTop, 2, TrackHeight));
        }
    }

    private void DrawTicks(DrawingContext context, TimelineViewport vp, double w, double h)
    {
        var step = TimelineTicks.ChooseStep(vp.VisibleSeconds, w);
        var stepSec = step.TotalSeconds;
        if (stepSec <= 0) return;

        // First aligned tick at or after VisibleStart (aligned to local time).
        var startLocal = vp.VisibleStart.ToLocalTime();
        var epoch = new DateTime(startLocal.Year, startLocal.Month, startLocal.Day, 0, 0, 0, DateTimeKind.Local);
        var sinceMidnight = (startLocal - epoch).TotalSeconds;
        var firstOffset = Math.Ceiling(sinceMidnight / stepSec) * stepSec;
        var tick = epoch.AddSeconds(firstOffset);

        var pen = new Pen(LabelBrush, 1);
        var labelY = h - 14;
        while (tick.ToUniversalTime() <= vp.VisibleEnd)
        {
            var x = vp.TimeToX(tick.ToUniversalTime(), w);
            context.DrawLine(pen, new Point(x, TrackTop + TrackHeight), new Point(x, TrackTop + TrackHeight + 4));
            var text = new FormattedText(FormatTick(tick, stepSec), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 10, LabelBrush);
            context.DrawText(text, new Point(x + 3, labelY));
            tick = tick.AddSeconds(stepSec);
        }
    }

    private void DrawPlayhead(DrawingContext context, TimelineViewport vp, double w)
    {
        if (Position is not { } pos) return;
        if (pos < vp.VisibleStart || pos > vp.VisibleEnd) return;
        var x = vp.TimeToX(pos, w);
        var pen = new Pen(PlayheadBrush, 2);
        context.DrawLine(pen, new Point(x, 0), new Point(x, TrackTop + TrackHeight + 4));
        // Small head triangle.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x - 4, 0), true);
            g.LineTo(new Point(x + 4, 0));
            g.LineTo(new Point(x, 6));
            g.EndFigure(true);
        }
        context.DrawGeometry(PlayheadBrush, null, geo);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var w = Bounds.Width;
        if (w <= 0) return;
        var anchorX = e.GetPosition(this).X;
        var factor = Math.Pow(1.2, e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X);
        Viewport.ZoomAt(anchorX, w, factor);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressPoint = e.GetPosition(this);
        _dragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var w = Bounds.Width;

        if (_pressPoint is { } start && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var dx = p.X - start.X;
            if (_dragging || Math.Abs(dx) > PanThresholdPx)
            {
                _dragging = true;
                Viewport.Pan(p.X - start.X, w);
                _pressPoint = p; // incremental pan
                InvalidateVisual();
            }
            return;
        }

        // Hover tooltip: nearest marker within a few pixels.
        var hit = HitTestMarker(p.X, w);
        ToolTip.SetTip(this, hit?.Label);
        ToolTip.SetIsOpen(this, hit is not null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var w = Bounds.Width;
        e.Pointer.Capture(null);

        if (!_dragging && _pressPoint is not null)
        {
            // Click: seek to the marker under the cursor, else to the raw time.
            var hit = HitTestMarker(p.X, w);
            var target = hit?.Time ?? Viewport.XToTime(p.X, w);
            if (SeekCommand?.CanExecute(target) == true)
                SeekCommand.Execute(target);
        }

        _pressPoint = null;
        _dragging = false;
    }

    private TimelineMarker? HitTestMarker(double x, double w)
    {
        var markers = Markers;
        if (markers is null || markers.Count == 0) return null;
        var vp = Viewport;
        TimelineMarker? best = null;
        var bestDist = MarkerHitPx;
        foreach (var m in markers)
        {
            if (m.Time < vp.VisibleStart || m.Time > vp.VisibleEnd) continue;
            var mx = vp.TimeToX(m.Time, w);
            var d = Math.Abs(mx - x);
            if (d <= bestDist)
            {
                bestDist = d;
                best = m;
            }
        }
        return best;
    }

    // Detection is the most prominent, then Motion, then Other.
    private static int Rank(TimelineMarkerKind k) => k switch
    {
        TimelineMarkerKind.Detection => 2,
        TimelineMarkerKind.Motion => 1,
        _ => 0,
    };

    private static TimelineMarkerKind Severer(TimelineMarkerKind a, TimelineMarkerKind b) =>
        Rank(a) >= Rank(b) ? a : b;

    private static IBrush BrushFor(TimelineMarkerKind kind) => kind switch
    {
        TimelineMarkerKind.Detection => DetectionBrush,
        TimelineMarkerKind.Motion => MotionBrush,
        _ => OtherBrush,
    };

    private static string FormatTick(DateTime localTick, double stepSec) =>
        stepSec >= 3600
            ? localTick.ToString("HH:mm", CultureInfo.CurrentCulture)
            : stepSec >= 60
                ? localTick.ToString("HH:mm", CultureInfo.CurrentCulture)
                : localTick.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
}
