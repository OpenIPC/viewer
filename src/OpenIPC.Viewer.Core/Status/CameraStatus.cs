namespace OpenIPC.Viewer.Core.Status;

// Single, view-agnostic health verdict for a camera. The grid (live session),
// the library/sidebar (TCP probe), and the future Health Center all derive
// their badge from this one enum instead of each rolling its own — see
// CameraStatusPolicy for how the raw signals collapse into it.
public enum CameraStatus
{
    // Never observed yet (idle tile pre-start, unprobed library row).
    Unknown = 0,

    // A connect or a reachability probe is in flight.
    Connecting,

    // Streaming, or the port answers and nothing contradicts it.
    Online,

    // Camera is reachable but the live stream is broken — alive but wedged.
    // Distinct from Offline so the UI can say "look at me" without crying wolf.
    Attention,

    // Stream failed with no positive signal, or the probe could not connect.
    Offline,
}

// Why the camera landed in its current status. Kept separate from the status so
// the Health Center can show a reason line ("stream error", "unreachable") and
// the grid/sidebar can stay reason-agnostic.
public enum CameraStatusReason
{
    None = 0,

    // A live session is mid-connect / mid-reconnect.
    Connecting,

    // A TCP reachability probe is running (no live session).
    Probing,

    // The live session faulted and the port does not answer.
    StreamError,

    // The live session faulted but the port still answers (-> Attention).
    StreamErrorButReachable,

    // The probe could not complete the TCP handshake.
    Unreachable,
}
