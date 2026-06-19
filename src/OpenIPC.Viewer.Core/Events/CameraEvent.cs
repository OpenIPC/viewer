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
public sealed record CameraEvent(
    EventId Id,
    CameraId CameraId,
    EventKind Kind,
    EventSeverity Severity,
    DateTime OccurredAt,
    DateTime? EndedAt,
    string? Source,
    string? Summary);
