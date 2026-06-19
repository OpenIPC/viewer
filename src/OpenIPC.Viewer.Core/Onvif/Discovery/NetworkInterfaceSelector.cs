using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenIPC.Viewer.Core.Onvif.Discovery;

// Platform-agnostic adapter snapshot — the impl in Devices builds these from
// System.Net.NetworkInformation; tests build them by hand. Keeps the selection
// rules (filter + ordering + bind pick) pure and unit-testable without a real
// network stack.
public readonly record struct NicDescriptor(
    string Name,
    bool IsUp,
    bool IsLoopback,
    bool IsTunnel,
    bool HasGateway,
    IReadOnlyList<string> IPv4Addresses);

// Pure WS-Discovery interface selection (Phase 12.6). Drops down/loopback/tunnel
// adapters and orders the survivors best-first: a gateway-bearing private-LAN
// adapter wins, a VPN/virtual adapter sinks. ResolveBindAddress turns a user
// preference (or "auto") into a concrete local IPv4 to bind the probe socket.
public static class NetworkInterfaceSelector
{
    public static IReadOnlyList<NetworkInterfaceInfo> SelectCandidates(IEnumerable<NicDescriptor> adapters)
    {
        var result = new List<(NetworkInterfaceInfo Info, bool HasGateway, bool Private)>();
        foreach (var a in adapters)
        {
            if (!a.IsUp || a.IsLoopback || a.IsTunnel) continue;
            if (a.IPv4Addresses is null) continue;
            foreach (var ip in a.IPv4Addresses)
            {
                if (string.IsNullOrWhiteSpace(ip)) continue;
                result.Add((new NetworkInterfaceInfo($"{a.Name} ({ip})", ip), a.HasGateway, IsPrivate(ip)));
            }
        }

        // Gateway-bearing private LAN adapters first; then private; then the rest.
        return result
            .OrderByDescending(x => x.HasGateway && x.Private)
            .ThenByDescending(x => x.Private)
            .ThenByDescending(x => x.HasGateway)
            .Select(x => x.Info)
            .ToList();
    }

    public static string? ResolveBindAddress(IReadOnlyList<NetworkInterfaceInfo> candidates, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) &&
            candidates.Any(c => string.Equals(c.Address, preferred, StringComparison.OrdinalIgnoreCase)))
            return preferred;

        // Auto (or a stale preference no longer present) → best candidate, or
        // null to mean "bind to any" when nothing usable was found.
        return candidates.Count > 0 ? candidates[0].Address : null;
    }

    private static bool IsPrivate(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
            return false;
        return a switch
        {
            10 => true,
            192 => b == 168,
            172 => b >= 16 && b <= 31,
            169 => b != 254, // exclude link-local autoconfig (169.254/16)
            _ => false,
        };
    }
}
