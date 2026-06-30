using System;

namespace OpenIPC.Viewer.Core.Discovery;

// How a device announced itself / was detected. Flags because one camera can be
// found by several sources at once (the aggregator ORs them together by IP).
[Flags]
public enum DiscoveryProtocol
{
    None = 0,
    Onvif = 1,
    Mdns = 2,
    Majestic = 4, // positive Majestic HTTP fingerprint (/api/v1/config.json)
    Rtsp = 8,     // RTSP port answered (554/8554)
    Http = 16,    // plain HTTP port answered (80/8080)
}
