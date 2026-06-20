using System;

namespace OpenIPC.Viewer.Core.Timeline;

// Pure presentation-model types for the archive timeline (Phase 16.4). No UI
// dependency — the control maps Kind to a brush. Times are UTC; the control
// formats labels in local time.

public enum TimelineMarkerKind
{
    Motion = 0,
    Detection,
    Other,
}

// A recorded span on the track.
public readonly record struct TimelineSegment(DateTime Start, DateTime End);

// A point event (motion / detection) rendered as a tick the user can click.
public readonly record struct TimelineMarker(DateTime Time, TimelineMarkerKind Kind, string? Label);
