using System;
using OpenIPC.Viewer.Core.Timeline;

namespace OpenIPC.Viewer.Core.Tests.Timeline;

// Time↔pixel mapping, cursor-anchored zoom, and clamping (Phase 16.4/16.7).
public sealed class TimelineViewportTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double sec) => T0.AddSeconds(sec);

    private static TimelineViewport Make() => new(T0, At(100)); // 100s total

    [Fact]
    public void TimeToX_MapsLinearlyAcrossWidth()
    {
        var vp = Make();
        Assert.Equal(0, vp.TimeToX(At(0), 1000), 3);
        Assert.Equal(500, vp.TimeToX(At(50), 1000), 3);
        Assert.Equal(1000, vp.TimeToX(At(100), 1000), 3);
    }

    [Fact]
    public void XToTime_RoundTripsTimeToX()
    {
        var vp = Make();
        var t = At(37.5);
        var x = vp.TimeToX(t, 800);
        var back = vp.XToTime(x, 800);
        Assert.Equal(t, back, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ZoomAt_KeepsTimeUnderCursorFixed()
    {
        var vp = Make();
        // Cursor at the middle → 50s. Zoom in 2×.
        vp.ZoomAt(anchorX: 500, width: 1000, factor: 2);
        Assert.Equal(50, vp.VisibleSeconds, 3);            // span halved
        Assert.Equal(500, vp.TimeToX(At(50), 1000), 1);    // anchor stays put
        Assert.Equal(At(25), vp.VisibleStart, TimeSpan.FromMilliseconds(5));
        Assert.Equal(At(75), vp.VisibleEnd, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void ZoomOut_ClampsToTotalRange()
    {
        var vp = Make();
        vp.ZoomAt(500, 1000, 4);    // zoom in first
        vp.ZoomAt(500, 1000, 0.01); // huge zoom-out
        Assert.Equal(100, vp.VisibleSeconds, 3);
        Assert.Equal(T0, vp.VisibleStart);
        Assert.Equal(At(100), vp.VisibleEnd);
    }

    [Fact]
    public void ZoomIn_ClampsToMinVisibleSeconds()
    {
        var vp = Make();
        vp.ZoomAt(500, 1000, 1_000_000);
        Assert.Equal(TimelineViewport.MinVisibleSeconds, vp.VisibleSeconds, 3);
    }

    [Fact]
    public void Pan_ClampsAtStartEdge()
    {
        var vp = Make();
        vp.ZoomAt(0, 1000, 2);              // 0..50s window anchored at left
        vp.Pan(deltaX: 500, width: 1000);  // drag right → window earlier, clamps at 0
        Assert.Equal(T0, vp.VisibleStart);
        Assert.Equal(50, vp.VisibleSeconds, 3);
    }
}
