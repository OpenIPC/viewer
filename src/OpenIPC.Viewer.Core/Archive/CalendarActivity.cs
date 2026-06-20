using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Archive;

// Per-day activity counts for the archive calendar (Phase 16.3). Date is a
// local calendar day (time component is midnight, Kind Unspecified).
public readonly record struct DayActivity(DateTime Date, int RecordingCount, int EventCount)
{
    public int Total => RecordingCount + EventCount;
    public bool HasActivity => Total > 0;
}

// Aggregates recordings + events into per-day counts for one month, in a given
// time zone. UTC→local conversion is DST-aware (TimeZoneInfo) — the competitor's
// recurring bug was mixing zones, so the boundary is converted explicitly here.
public static class CalendarActivity
{
    public static IReadOnlyDictionary<DateTime, DayActivity> ForMonth(
        int year, int month,
        IEnumerable<DateTime> recordingStartsUtc,
        IEnumerable<DateTime> eventTimesUtc,
        TimeZoneInfo timeZone)
    {
        var result = new Dictionary<DateTime, DayActivity>();

        void Tally(DateTime utc, bool isRecording)
        {
            var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, timeZone);
            if (local.Year != year || local.Month != month)
                return;
            var day = local.Date;
            result.TryGetValue(day, out var cur);
            result[day] = isRecording
                ? new DayActivity(day, cur.RecordingCount + 1, cur.EventCount)
                : new DayActivity(day, cur.RecordingCount, cur.EventCount + 1);
        }

        foreach (var r in recordingStartsUtc) Tally(r, isRecording: true);
        foreach (var e in eventTimesUtc) Tally(e, isRecording: false);
        return result;
    }

    // Normalized 0..1 intensity of a day's total against the busiest day's total
    // (for shading). Returns 0 when there's no activity in the set.
    public static double Intensity(DayActivity day, int maxTotal) =>
        maxTotal <= 0 ? 0 : Math.Clamp(day.Total / (double)maxTotal, 0.0, 1.0);
}
