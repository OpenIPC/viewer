using System;

namespace OpenIPC.Viewer.Core.Timeline;

// Picks an adaptive label interval for the timeline (Phase 16.4): hours →
// minutes → seconds as the user zooms. Pure + testable.
public static class TimelineTicks
{
    // "Nice" steps in seconds, ascending: 1s..24h.
    private static readonly double[] StepsSeconds =
    {
        1, 2, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600, 7200, 10800, 21600, 43200, 86400,
    };

    // Smallest step whose on-screen spacing is at least minSpacingPx.
    public static TimeSpan ChooseStep(double visibleSeconds, double width, double minSpacingPx = 80)
    {
        if (width <= 0 || visibleSeconds <= 0)
            return TimeSpan.FromSeconds(StepsSeconds[^1]);

        var targetSeconds = visibleSeconds * minSpacingPx / width;
        foreach (var s in StepsSeconds)
            if (s >= targetSeconds)
                return TimeSpan.FromSeconds(s);
        return TimeSpan.FromSeconds(StepsSeconds[^1]);
    }
}
