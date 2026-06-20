using System;

namespace OpenIPC.Viewer.Core.Timeline;

// Maps a visible time window to pixels and back, with cursor-anchored zoom and
// pan (Phase 16.4). Pure math (doubles + DateTime) so it's unit-testable
// without a control. All clamping keeps the visible window inside the total
// range; zoom keeps the time under the anchor pixel fixed.
public sealed class TimelineViewport
{
    // Tightest zoom-in: a 2-second visible window. Prevents div-by-zero and
    // runaway zoom.
    public const double MinVisibleSeconds = 2.0;

    public DateTime TotalStart { get; }
    public DateTime TotalEnd { get; }
    public DateTime VisibleStart { get; private set; }
    public DateTime VisibleEnd { get; private set; }

    public TimelineViewport(DateTime totalStart, DateTime totalEnd)
    {
        if (totalEnd <= totalStart)
            totalEnd = totalStart.AddSeconds(MinVisibleSeconds);
        TotalStart = totalStart;
        TotalEnd = totalEnd;
        VisibleStart = totalStart;
        VisibleEnd = totalEnd;
    }

    public double TotalSeconds => (TotalEnd - TotalStart).TotalSeconds;
    public double VisibleSeconds => (VisibleEnd - VisibleStart).TotalSeconds;

    public double TimeToX(DateTime t, double width) =>
        (t - VisibleStart).TotalSeconds / VisibleSeconds * width;

    public DateTime XToTime(double x, double width)
    {
        if (width <= 0) return VisibleStart;
        return VisibleStart.AddSeconds(x / width * VisibleSeconds);
    }

    // factor > 1 zooms in (window shrinks), < 1 zooms out. The time under
    // anchorX stays at anchorX.
    public void ZoomAt(double anchorX, double width, double factor)
    {
        if (width <= 0 || factor <= 0) return;
        var anchorTime = XToTime(anchorX, width);
        var newSpan = Math.Clamp(VisibleSeconds / factor, MinVisibleSeconds, TotalSeconds);
        var frac = Math.Clamp(anchorX / width, 0.0, 1.0);
        var newStart = anchorTime.AddSeconds(-frac * newSpan);
        SetWindow(newStart, newSpan);
    }

    // Pans by a pixel delta: dragging right (positive deltaX) moves the content
    // right, i.e. the window moves earlier in time.
    public void Pan(double deltaX, double width)
    {
        if (width <= 0) return;
        var dtSec = deltaX / width * VisibleSeconds;
        SetWindow(VisibleStart.AddSeconds(-dtSec), VisibleSeconds);
    }

    public void Reset()
    {
        VisibleStart = TotalStart;
        VisibleEnd = TotalEnd;
    }

    // Places a window of the given span starting at desiredStart, clamped so it
    // stays within [TotalStart, TotalEnd]. Span is also clamped to the total.
    private void SetWindow(DateTime desiredStart, double spanSeconds)
    {
        var span = Math.Clamp(spanSeconds, MinVisibleSeconds, TotalSeconds);
        var start = desiredStart;
        if (start < TotalStart) start = TotalStart;
        var end = start.AddSeconds(span);
        if (end > TotalEnd)
        {
            end = TotalEnd;
            start = end.AddSeconds(-span);
            if (start < TotalStart) start = TotalStart;
        }
        VisibleStart = start;
        VisibleEnd = end;
    }
}
