namespace OpenIPC.Viewer.Core.Discovery;

// How sure we are that a discovered host is actually an IP camera, derived from
// how many independent signals agree (and whether an app-layer fingerprint hit).
public enum DiscoveryConfidence
{
    Low,    // a single weak signal (e.g. only an RTSP port open)
    Medium, // two independent signals
    High,   // a positive Majestic or ONVIF fingerprint — we know what it is
}
