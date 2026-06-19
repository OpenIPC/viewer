using System;
using System.Threading;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Events;

// A pluggable source of raw motion ticks for a single camera. The source emits
// once per detected motion sample — debounce / dedup / event-closing all happen
// in EventIngestionService so different sources stay simple.
//
// Real implementations land per-camera-protocol (Phase 7 §7.1): Majestic
// polling, ONVIF PullPoint, syslog listener. For now only the manual-trigger
// shim exists so the ingestion service has something to test against.
public interface IMotionEventSource
{
    string Name { get; }

    // Returns a hot observable of motion ticks. Implementation owns the polling
    // loop / subscription; Dispose of the returned IDisposable to stop.
    IDisposable Watch(CameraId cameraId, IObserver<MotionTick> observer, CancellationToken ct);
}

// Kind/Summary default to a plain motion tick; analytics sources set Kind to
// Detection and Summary to the class counts (Phase 15.7) so the same ingestion
// path produces both motion and detection events.
public readonly record struct MotionTick(
    CameraId CameraId,
    DateTime At,
    string Source,
    EventKind Kind = EventKind.Motion,
    string? Summary = null);
