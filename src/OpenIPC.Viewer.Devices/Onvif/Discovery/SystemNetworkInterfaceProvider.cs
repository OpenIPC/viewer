using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpenIPC.Viewer.Core.Onvif.Discovery;

namespace OpenIPC.Viewer.Devices.Onvif.Discovery;

// Builds NicDescriptors from System.Net.NetworkInformation and defers the
// filter/order/select rules to the pure NetworkInterfaceSelector (Phase 12.6).
// Enumeration is best-effort: a platform that restricts it (some mobile
// sandboxes) yields an empty list → discovery falls back to binding "any".
public sealed class SystemNetworkInterfaceProvider : INetworkInterfaceProvider
{
    public IReadOnlyList<NetworkInterfaceInfo> GetCandidates()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return Array.Empty<NetworkInterfaceInfo>(); }

        var adapters = new List<NicDescriptor>(nics.Length);
        foreach (var nic in nics)
        {
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            var ipv4 = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address.ToString())
                .ToList();
            if (ipv4.Count == 0) continue;

            var hasGateway = props.GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

            adapters.Add(new NicDescriptor(
                Name: nic.Name,
                IsUp: nic.OperationalStatus == OperationalStatus.Up,
                IsLoopback: nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                IsTunnel: nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel,
                HasGateway: hasGateway,
                IPv4Addresses: ipv4));
        }

        return NetworkInterfaceSelector.SelectCandidates(adapters);
    }
}
