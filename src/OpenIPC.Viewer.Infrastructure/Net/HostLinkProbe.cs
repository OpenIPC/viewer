using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpenIPC.Viewer.Core.LinkHealth;

namespace OpenIPC.Viewer.Infrastructure.Net;

// Finds the adapter of this computer that reaches a camera and reads its link
// speed and error counters. The adapter is the one whose IPv4 subnet holds the
// camera; failing that, the one the OS picks for a UDP "connect" to it (no
// packet is sent). Counters a platform doesn't implement stay null.
public sealed class HostLinkProbe : IHostLinkProbe
{
    public async Task<HostLinkSnapshot?> GetAsync(string cameraHost, string? localAddress, CancellationToken ct)
    {
        var target = await ResolveAsync(cameraHost, ct).ConfigureAwait(false);
        if (target is null) return null;

        // A known address on the camera's network wins over the OS route choice,
        // which can point at the wrong adapter (two adapters in one subnet).
        var local = localAddress is not null && IPAddress.TryParse(localAddress, out var hint)
            ? hint
            : LocalAddressFor(target);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var match = local is not null
                    ? ua.Address.Equals(local)
                    : SameSubnet(ua.Address, target, ua.PrefixLength);
                if (match)
                    return Snapshot(nic, ua.Address);
            }
        }
        return null;
    }

    private static HostLinkSnapshot Snapshot(NetworkInterface nic, IPAddress local)
    {
        long? speed = null;
        try { speed = nic.Speed > 0 ? nic.Speed / 1_000_000 : null; } catch (PlatformNotSupportedException) { }

        long? rxErr = null, rxDisc = null, txErr = null, txDisc = null, rxPk = null, txPk = null;
        try
        {
            var st = nic.GetIPStatistics();
            rxErr = Try(() => st.IncomingPacketsWithErrors);
            rxDisc = Try(() => st.IncomingPacketsDiscarded);
            txErr = Try(() => st.OutgoingPacketsWithErrors);
            if (!OperatingSystem.IsMacOS())
            {
                try { txDisc = st.OutgoingPacketsDiscarded; }
                catch (PlatformNotSupportedException) { }
            }
            rxPk = Try(() => st.UnicastPacketsReceived);
            txPk = Try(() => st.UnicastPacketsSent);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
        }

        return new HostLinkSnapshot(
            nic.Name, nic.Description, nic.OperationalStatus == OperationalStatus.Up, speed,
            local.ToString(), rxErr, rxDisc, txErr, txDisc, rxPk, txPk);
    }

    private static long? Try(Func<long> read)
    {
        try { return read(); }
        catch (PlatformNotSupportedException) { return null; }
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static IPAddress? LocalAddressFor(IPAddress target)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        if (prefix <= 0 || prefix > 32) return false;
        var mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        return (ToUInt(a) & mask) == (ToUInt(b) & mask);
    }

    private static uint ToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
