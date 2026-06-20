using System;
using OpenIPC.Viewer.Core.Timeline;

namespace OpenIPC.Viewer.Core.Tests.Timeline;

// Adaptive label step: seconds when zoomed in, hours when zoomed out (16.4).
public sealed class TimelineTicksTests
{
    [Fact]
    public void ZoomedIn_ChoosesSecondsScaleStep()
    {
        // 20s visible over 1000px, ≥80px spacing → ≥1.6s target → 2s step.
        var step = TimelineTicks.ChooseStep(visibleSeconds: 20, width: 1000, minSpacingPx: 80);
        Assert.Equal(TimeSpan.FromSeconds(2), step);
    }

    [Fact]
    public void ZoomedOut_ChoosesCoarseStep()
    {
        // A full day over 1000px → target 6912s → smallest nice step ≥ that is 2h.
        var step = TimelineTicks.ChooseStep(visibleSeconds: 86400, width: 1000, minSpacingPx: 80);
        Assert.Equal(TimeSpan.FromHours(2), step);
    }

    [Fact]
    public void DegenerateWidth_ReturnsMaxStep()
    {
        var step = TimelineTicks.ChooseStep(visibleSeconds: 100, width: 0);
        Assert.Equal(TimeSpan.FromHours(24), step);
    }
}
