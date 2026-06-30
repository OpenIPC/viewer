using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Services;

// Resolves the network endpoint a camera is actually dialed on. Domain knowledge
// that bit us before: the RTSP URI host can differ from Camera.Host (ONVIF behind
// NAT, an mDNS name vs a raw IP), and the URI may omit the port. Centralised so
// the probe path (grid, single, library, Health Center) can't drift apart.
public static class CameraEndpoints
{
    public const int DefaultRtspPort = 554;

    // The host/port the RTSP player dials: prefer the RTSP URI, fall back to the
    // Host field and the default RTSP port when the URI omits them.
    public static (string Host, int Port) StreamProbeTarget(this Camera camera)
    {
        var host = camera.RtspMainUri.Host;
        if (string.IsNullOrEmpty(host)) host = camera.Host;
        var port = camera.RtspMainUri.Port;
        if (port <= 0) port = DefaultRtspPort;
        return (host, port);
    }
}
