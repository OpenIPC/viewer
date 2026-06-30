using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Status;

public readonly record struct CameraStatusSnapshot(CameraId CameraId, CameraStatusResult Result);

// Process-wide merge point for camera status. A camera's verdict is fed by two
// independent sources — a live video session (grid tile / single view) and a TCP
// reachability probe (library / Health Center) — and the registry holds the raw
// inputs per camera, re-resolving through CameraStatusPolicy on every report.
//
// This is what lets a wedged stream observed by a grid tile (Attention) show up
// in the library and Health Center, which otherwise only see their own probe.
// Thread-safe; Changed fires (outside the lock) only when the resolved verdict
// actually moves, so subscribers aren't woken on no-op reports.
public sealed class CameraStatusRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<CameraId, Inputs> _inputs = new();
    private readonly Dictionary<CameraId, CameraStatusResult> _resolved = new();

    public event EventHandler<CameraStatusSnapshot>? Changed;

    // The last resolved verdict for a camera, or Unknown if nothing reported yet.
    public CameraStatusResult Get(CameraId id)
    {
        lock (_gate)
            return _resolved.TryGetValue(id, out var r)
                ? r
                : new CameraStatusResult(CameraStatus.Unknown, CameraStatusReason.None);
    }

    // A live session reported a new state. Pass null to clear the session signal
    // (e.g. the tile was torn down) and fall back to the probe input alone.
    public void ReportSession(CameraId id, SessionState? state) =>
        Update(id, prev => prev with { Session = state });

    // A reachability probe reported a result (or that one is now in flight).
    public void ReportReachability(CameraId id, bool? reachable, bool probeInFlight = false) =>
        Update(id, prev => prev with { Reachable = reachable, ProbeInFlight = probeInFlight });

    private void Update(CameraId id, Func<Inputs, Inputs> mutate)
    {
        CameraStatusSnapshot? changed = null;
        lock (_gate)
        {
            _inputs.TryGetValue(id, out var prev);
            var next = mutate(prev);
            _inputs[id] = next;

            var result = CameraStatusPolicy.Resolve(
                new CameraStatusInputs(next.Session, next.Reachable, next.ProbeInFlight));

            if (!_resolved.TryGetValue(id, out var old) || !old.Equals(result))
            {
                _resolved[id] = result;
                changed = new CameraStatusSnapshot(id, result);
            }
        }

        if (changed is { } snapshot)
            Changed?.Invoke(this, snapshot);
    }

    private readonly record struct Inputs(SessionState? Session, bool? Reachable, bool ProbeInFlight);
}
