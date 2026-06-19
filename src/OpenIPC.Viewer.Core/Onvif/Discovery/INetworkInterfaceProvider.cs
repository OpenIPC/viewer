using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Onvif.Discovery;

// Enumerates usable LAN network interfaces for binding WS-Discovery and other
// listeners (Phase 12.6). Implementations filter out tunnel/loopback/down
// adapters so discovery doesn't leave via a VPN on multi-adapter machines.
// Candidates are ordered best-first (gateway-bearing LAN adapters first).
public interface INetworkInterfaceProvider
{
    IReadOnlyList<NetworkInterfaceInfo> GetCandidates();
}

// DisplayName is shown in Settings; Address is the local IPv4 used both as the
// persisted setting value and the socket bind target.
public sealed record NetworkInterfaceInfo(string DisplayName, string Address);
