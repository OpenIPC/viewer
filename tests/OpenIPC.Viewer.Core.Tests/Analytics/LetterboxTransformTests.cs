using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Core.Tests.Analytics;

// Letterbox geometry + inverse mapping (Phase 15.1). Boxes come out of the
// model in input-pixel space; MapToSource must undo the aspect-preserving
// scale + centered pad back into source-normalized 0..1 coords.
public sealed class LetterboxTransformTests
{
    [Fact]
    public void Square_NoPadding_FullBoxMapsToWholeFrame()
    {
        var lb = new LetterboxTransform(100, 100, 416, 416);
        Assert.Equal(4.16f, lb.Scale, 3);
        Assert.Equal(0f, lb.PadX, 3);
        Assert.Equal(0f, lb.PadY, 3);

        var det = lb.MapToSource(0, "person", 1f, inX: 0, inY: 0, inW: 416, inH: 416);
        Assert.Equal(0f, det.X, 4);
        Assert.Equal(0f, det.Y, 4);
        Assert.Equal(1f, det.Width, 4);
        Assert.Equal(1f, det.Height, 4);
    }

    [Fact]
    public void Wide_Source_PadsVertically_AndInverseMapsBack()
    {
        // 200×100 into 100×100: scale 0.5, scaled 100×50, vertical pad 25 each.
        var lb = new LetterboxTransform(200, 100, 100, 100);
        Assert.Equal(0.5f, lb.Scale, 3);
        Assert.Equal(0f, lb.PadX, 3);
        Assert.Equal(25f, lb.PadY, 3);

        var det = lb.MapToSource(2, "car", 0.8f, inX: 0, inY: 25, inW: 100, inH: 50);
        Assert.Equal(0f, det.X, 4);
        Assert.Equal(0f, det.Y, 4);
        Assert.Equal(1f, det.Width, 4);
        Assert.Equal(1f, det.Height, 4);
    }

    [Fact]
    public void MapToSource_ClampsToFrame()
    {
        var lb = new LetterboxTransform(100, 100, 100, 100);
        // Box partly outside the frame is clamped to [0,1].
        var det = lb.MapToSource(0, "person", 1f, inX: -20, inY: -20, inW: 60, inH: 60);
        Assert.Equal(0f, det.X, 4);
        Assert.Equal(0f, det.Y, 4);
        Assert.Equal(0.4f, det.Width, 4);  // (-20+40)/100 = 0.4
        Assert.Equal(0.4f, det.Height, 4);
    }
}
