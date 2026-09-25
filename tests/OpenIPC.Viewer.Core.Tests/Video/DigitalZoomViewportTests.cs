using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Tests.Video;

// Anchored zoom, pan clamping and letterbox handling for the live-view digital zoom.
public sealed class DigitalZoomViewportTests
{
    // 1000×500 view showing a 2:1 picture — no letterbox bars.
    private static DigitalZoomViewport Make(double aspect = 2.0)
    {
        var vp = new DigitalZoomViewport();
        vp.SetViewSize(1000, 500);
        vp.SetContentAspect(aspect);
        return vp;
    }

    [Fact]
    public void ZoomAt_KeepsPointUnderAnchorFixed()
    {
        var vp = Make();
        var before = vp.ScreenToView(300, 200);

        vp.ZoomAt(300, 200, 2);

        Assert.Equal(2, vp.Zoom, 6);
        var after = vp.ScreenToView(300, 200);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void Zoom_IsClampedToRange()
    {
        var vp = Make();
        vp.ZoomAt(500, 250, 100);
        Assert.Equal(DigitalZoomViewport.DefaultMaxZoom, vp.Zoom, 6);

        vp.ZoomAt(500, 250, 0.001);
        Assert.Equal(DigitalZoomViewport.MinZoom, vp.Zoom, 6);
        Assert.False(vp.IsZoomed);
        Assert.Equal(0, vp.OffsetX, 6);
        Assert.Equal(0, vp.OffsetY, 6);
    }

    [Fact]
    public void Pan_CannotLeaveThePicture()
    {
        var vp = Make();
        vp.ZoomAt(500, 250, 2);

        vp.Pan(10_000, 10_000); // drag far right/down → window pinned top-left
        Assert.Equal(0, vp.OffsetX, 6);
        Assert.Equal(0, vp.OffsetY, 6);

        vp.Pan(-10_000, -10_000); // far left/up → window pinned bottom-right
        Assert.Equal(500, vp.OffsetX, 6); // 1000 - 1000/2
        Assert.Equal(250, vp.OffsetY, 6);
    }

    [Fact]
    public void Pan_MovesWindowOppositeToDrag()
    {
        var vp = Make();
        vp.ZoomAt(500, 250, 4);
        var x = vp.OffsetX;

        vp.Pan(40, 0); // 40 screen px at 4× = 10 view units

        Assert.Equal(x - 10, vp.OffsetX, 6);
    }

    [Fact]
    public void Pillarbox_BarsStayCenteredUntilPictureFillsTheView()
    {
        // 1:1 picture in a 1000×500 view → 500 wide, bars of 250 each side.
        var vp = Make(aspect: 1.0);
        Assert.Equal((250.0, 0.0, 500.0, 500.0), vp.ContentRect);

        // At 1.5× the visible window is 666 wide, still wider than the picture:
        // horizontally it stays centered whatever the anchor.
        vp.ZoomAt(0, 250, 1.5);
        Assert.Equal(500 - 1000 / 1.5 / 2, vp.OffsetX, 6);

        // At 4× the window (250) is narrower than the picture: it's clamped
        // onto it, so a bar can't be dragged into view.
        vp.ZoomAt(500, 250, 4 / 1.5);
        vp.Pan(10_000, 0);
        Assert.Equal(250, vp.OffsetX, 6);
    }

    [Fact]
    public void ZoomToRect_FitsSelectionAndCentersOnIt()
    {
        var vp = Make();

        // Right-bottom quarter of the view.
        vp.ZoomToRect(500, 250, 500, 250);

        Assert.Equal(2, vp.Zoom, 6);
        Assert.Equal(500, vp.OffsetX, 6);
        Assert.Equal(250, vp.OffsetY, 6);
    }

    [Fact]
    public void ZoomToRect_KeepsWholeSelectionVisible()
    {
        var vp = Make();

        // Tall, narrow selection: height decides the zoom.
        vp.ZoomToRect(450, 0, 100, 250);

        Assert.Equal(2, vp.Zoom, 6);
    }

    [Fact]
    public void VisibleContent_ReportsNormalizedWindow()
    {
        var vp = Make();
        vp.ZoomToRect(500, 250, 500, 250);

        var (x, y, w, h) = vp.VisibleContent;
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);
        Assert.Equal(0.5, w, 6);
        Assert.Equal(0.5, h, 6);
    }

    [Fact]
    public void CenterOn_MovesWindowToNormalizedPoint()
    {
        var vp = Make();
        vp.ZoomAt(0, 0, 4);

        vp.CenterOn(0.5, 0.5);

        Assert.Equal(500 - 125, vp.OffsetX, 6);
        Assert.Equal(250 - 62.5, vp.OffsetY, 6);
    }

    [Fact]
    public void SetViewSize_KeepsSamePartOfPictureOnScreen()
    {
        var vp = Make();
        vp.ZoomToRect(500, 250, 500, 250);
        var before = vp.VisibleContent;

        vp.SetViewSize(2000, 1000);

        var after = vp.VisibleContent;
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
        Assert.Equal(before.Width, after.Width, 6);
    }
}
