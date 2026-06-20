using System;
using OpenIPC.Viewer.Core.Archive;

namespace OpenIPC.Viewer.Core.Tests.Archive;

// Keyframe-boundary math for stream-copy export (Phase 16.5/16.7).
public sealed class ClipBoundsTests
{
    private static readonly TimeSpan[] Keyframes =
    {
        TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(8),
    };

    [Fact]
    public void SnapStart_PicksLastKeyframeAtOrBefore()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), ClipBounds.SnapStartToKeyframe(TimeSpan.FromSeconds(5), Keyframes));
        Assert.Equal(TimeSpan.FromSeconds(2), ClipBounds.SnapStartToKeyframe(TimeSpan.FromSeconds(2), Keyframes));
        Assert.Equal(TimeSpan.Zero, ClipBounds.SnapStartToKeyframe(TimeSpan.FromSeconds(1), Keyframes));
    }

    [Fact]
    public void SnapStart_BeyondLastKeyframe_ReturnsLast()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), ClipBounds.SnapStartToKeyframe(TimeSpan.FromSeconds(20), Keyframes));
    }

    [Fact]
    public void Duration_IsZeroWhenEndNotAfterStart()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), ClipBounds.Duration(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.Zero, ClipBounds.Duration(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.Zero, ClipBounds.Duration(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
    }
}
