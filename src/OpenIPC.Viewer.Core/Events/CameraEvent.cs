using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Events;

public enum EventKind
{
    Motion = 0,
    Connection,    // RTSP connect / disconnect (future)
    Snapshot,      // user-initiated snapshot (future)
    Detection,     // AI object detection (Phase 15) — Summary holds class counts
}

public enum EventSeverity
{
    Info = 0,
    Warning,
}

// "Event" alone collides with System.Event-related conventions; CameraEvent is
// explicit about scope.
//
// PeakConfidence / PeakObjects are the best values seen so far in the episode
// (detection events only); LastSeenAt is the latest tick of a still-open
// episode, i.e. how long the activity has actually lasted. They live on the
// in-memory stream for the notification thresholds and are not persisted.
public sealed record CameraEvent(
    EventId Id,
    CameraId CameraId,
    EventKind Kind,
    EventSeverity Severity,
    DateTime OccurredAt,
    DateTime? EndedAt,
    string? Source,
    string? Summary,
    float? PeakConfidence = null,
    int PeakObjects = 0,
    DateTime? LastSeenAt = null);
