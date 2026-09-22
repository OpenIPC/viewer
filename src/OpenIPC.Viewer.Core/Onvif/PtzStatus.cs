using System;

namespace OpenIPC.Viewer.Core.Onvif;

public enum PtzMoveState
{
    // The camera does not report a usable MoveStatus — it is optional in the
    // spec, and several brands return a value that never changes.
    Unknown = 0,
    Idle,
    Moving,
}

// A camera's answer to GetStatus: where it is pointing, and whether it is still
// on its way there.
//
// Positions are in the camera's own declared units — degrees, [-1, 1], or
// whatever its absolute spaces say — exactly as the response carried them. A
// consumer that wants them normalized maps them through the ranges the
// capability probe read (PtzRange.ToUnit); the client does not, because it
// would have to re-fetch those ranges on every poll to do it.
public sealed record PtzStatus(
    float Pan,
    float Tilt,
    float Zoom,
    PtzMoveState PanTilt,
    PtzMoveState ZoomState,
    DateTime? UtcTime)
{
    public bool IsMoving => PanTilt == PtzMoveState.Moving || ZoomState == PtzMoveState.Moving;
}
