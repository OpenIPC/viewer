using System;
using System.Linq;
using OpenIPC.Viewer.Core.Archive;

namespace OpenIPC.Viewer.Core.Tests.Archive;

// Per-day aggregation for the archive calendar (Phase 16.3/16.7). UTC zone keeps
// the local/UTC boundary deterministic across machines.
public sealed class CalendarActivityTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static DateTime U(int y, int m, int d, int h = 12) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ForMonth_CountsRecordingsAndEventsPerDay()
    {
        var recordings = new[] { U(2026, 1, 5), U(2026, 1, 5, 14), U(2026, 1, 6) };
        var events = new[] { U(2026, 1, 5, 9), U(2026, 2, 1) }; // Feb event excluded

        var result = CalendarActivity.ForMonth(2026, 1, recordings, events, Utc);

        var jan5 = result[new DateTime(2026, 1, 5)];
        Assert.Equal(2, jan5.RecordingCount);
        Assert.Equal(1, jan5.EventCount);
        Assert.Equal(3, jan5.Total);

        var jan6 = result[new DateTime(2026, 1, 6)];
        Assert.Equal(1, jan6.RecordingCount);
        Assert.Equal(0, jan6.EventCount);

        Assert.False(result.ContainsKey(new DateTime(2026, 2, 1)));
    }

    [Fact]
    public void ForMonth_IgnoresOtherMonths()
    {
        var recordings = new[] { U(2025, 12, 31), U(2026, 2, 1) };
        var result = CalendarActivity.ForMonth(2026, 1, recordings, Array.Empty<DateTime>(), Utc);
        Assert.Empty(result);
    }

    [Fact]
    public void Intensity_NormalizesAgainstBusiestDay()
    {
        var recordings = new[] { U(2026, 1, 5), U(2026, 1, 5), U(2026, 1, 5), U(2026, 1, 6) };
        var result = CalendarActivity.ForMonth(2026, 1, recordings, Array.Empty<DateTime>(), Utc);
        var max = result.Values.Max(d => d.Total);

        Assert.Equal(1.0, CalendarActivity.Intensity(result[new DateTime(2026, 1, 5)], max), 3);
        Assert.Equal(1.0 / 3, CalendarActivity.Intensity(result[new DateTime(2026, 1, 6)], max), 3);
    }
}
