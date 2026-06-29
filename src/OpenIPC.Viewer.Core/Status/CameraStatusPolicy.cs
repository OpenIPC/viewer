using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Status;

// The two raw status signals fed to the policy. Either or both may be present:
//   - a grid/single-view tile carries a live <see cref="SessionState"/>;
//   - a library/sidebar row carries a TCP <see cref="Reachable"/> probe result.
// All fields default to "no signal", so a caller supplies only what it has.
public readonly record struct CameraStatusInputs(
    // Live video session state, or null when no session is attached (library row).
    SessionState? Session = null,
    // Last TCP reachability probe: true/false, or null when never probed.
    bool? Reachable = null,
    // A reachability probe is currently running (library row mid-check).
    bool ProbeInFlight = false);

public readonly record struct CameraStatusResult(CameraStatus Status, CameraStatusReason Reason);

// Collapses the live session state and the TCP reachability probe into one
// <see cref="CameraStatus"/>. Pure and clock-free so it unit-tests trivially and
// gives the grid, the sidebar, and the Health Center one source of truth.
//
// Core rule: a live session is authoritative. A faulted stream is NEVER masked
// by a (possibly stale) optimistic "reachable" — the worst credible signal wins,
// so a wedged camera reads Attention/Offline rather than a lingering green LIVE.
public static class CameraStatusPolicy
{
    public static CameraStatusResult Resolve(in CameraStatusInputs input)
    {
        // 1. A live session reflects the actual decoded stream, not a port probe,
        //    so it outranks reachability whenever one is attached.
        if (input.Session is { } state)
        {
            switch (state)
            {
                case SessionState.Playing:
                case SessionState.Paused: // Smart-Paused = alive, just not decoding.
                    return new(CameraStatus.Online, CameraStatusReason.None);

                case SessionState.Connecting:
                case SessionState.Reconnecting:
                    return new(CameraStatus.Connecting, CameraStatusReason.Connecting);

                case SessionState.Failed:
                    // Stream broke. If the port still answers the camera is alive
                    // but wedged (Attention); otherwise it's genuinely Offline.
                    return input.Reachable == true
                        ? new(CameraStatus.Attention, CameraStatusReason.StreamErrorButReachable)
                        : new(CameraStatus.Offline, CameraStatusReason.StreamError);

                case SessionState.Idle:
                default:
                    break; // pre-start: fall through to the probe-based verdict.
            }
        }

        // 2. No authoritative live signal — lean on the TCP probe (library rows).
        if (input.ProbeInFlight)
            return new(CameraStatus.Connecting, CameraStatusReason.Probing);

        return input.Reachable switch
        {
            true => new(CameraStatus.Online, CameraStatusReason.None),
            false => new(CameraStatus.Offline, CameraStatusReason.Unreachable),
            null => new(CameraStatus.Unknown, CameraStatusReason.None),
        };
    }
}
