using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.LinkHealth;

namespace OpenIPC.Viewer.Devices.LinkHealth;

// Finds out, per camera, where this computer sends its packets and on which
// adapter the camera really answers — the check behind "two adapters in one
// subnet". Windows does both per adapter: SendARP asks a specific adapter's
// network ("who has 192.168.1.141?" out of Ethernet, then out of Wi-Fi), and
// a TCP connect bound to an adapter's address leaves by that adapter (Windows
// sends by source address — strong host model). Other OSes route by table
// whatever the source, so there the camera is touched through the normal
// route and located from the kernel's ARP cache.
//
// Also reports our own addresses that some other device answers for (an IP
// conflict across the two networks).
public sealed class HostRoutingProbe : IHostRoutingProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly ILogger<HostRoutingProbe> _logger;
    private readonly Discovery.Routes.IRouteTableReader? _routes;

    /// <param name="routes">The OS route table (null where there is no reader) — to see host routes that pin a camera.</param>
    public HostRoutingProbe(ILogger<HostRoutingProbe> logger, Discovery.Routes.IRouteTableReader? routes = null)
    {
        _logger = logger;
        _routes = routes;
    }

    public async Task<HostRoutingSnapshot> ProbeAsync(IReadOnlyList<RouteTarget> targets, CancellationToken ct)
    {
        var (nics, ownMacs) = ReadInterfaces();
        var pinned = ReadHostRoutes();
        // Both wait on ARP timeouts (~3 s for a silent address), so run them together.
        var activeConflicts = AskForOwnAddressesAsync(nics, ownMacs, ct);
        var probed = await Task.WhenAll(targets.Select(t => ProbeCameraAsync(t, nics, ct))).ConfigureAwait(false);
        var cameras = probed
            .Select(c => c.Ip is not null && pinned.TryGetValue(c.Ip, out var iface) ? c with { PinnedInterface = iface } : c)
            .ToArray();

        // Read the cache after probing so it holds what the probes just learned.
        var arp = ReadArpTable(nics);
        var located = OperatingSystem.IsWindows()
            ? cameras.ToList()
            : cameras.Select(c => FillFromCache(c, arp)).ToList();

        var conflicts = PassiveConflicts(nics, ownMacs, arp, await activeConflicts.ConfigureAwait(false));
        return new HostRoutingSnapshot(nics.Select(n => n.Info).ToList(), located, conflicts);
    }

    private async Task<CameraRouteProbe> ProbeCameraAsync(RouteTarget target, IReadOnlyList<Nic> nics, CancellationToken ct)
    {
        var ip = await ResolveAsync(target.Host, ct).ConfigureAwait(false);
        if (ip is null)
            return new CameraRouteProbe(target.Host, null, target.Port, null, null, Array.Empty<InterfaceProbe>());

        var routedLocal = LocalAddressFor(ip);
        var routed = routedLocal is null ? null : nics.FirstOrDefault(n => n.V4.Any(a => a.Address.Equals(routedLocal)));

        var candidates = nics
            .SelectMany(n => n.V4.Where(a => SameSubnet(a.Address, ip, a.Prefix)).Select(a => (Nic: n, a.Address)))
            .ToList();

        var probes = await Task.WhenAll(candidates.Select(async c =>
        {
            string? mac = null;
            bool? stream = null;
            if (OperatingSystem.IsWindows())
            {
                var arpTask = Task.Run(() => OperatingSystem.IsWindows() ? SendArp(ip, c.Address) : null, ct);
                var tcpTask = ConnectAsync(c.Address, ip, target.Port, ct);
                await Task.WhenAll(arpTask, tcpTask).ConfigureAwait(false);
                mac = arpTask.Result;
                stream = tcpTask.Result;
            }
            else if (routed is not null && ReferenceEquals(c.Nic, routed))
            {
                // Weak host model: only the routed adapter can be tried.
                stream = await ConnectAsync(null, ip, target.Port, ct).ConfigureAwait(false);
            }
            return new InterfaceProbe(c.Nic.Info.Name, c.Nic.Info.Index, c.Address.ToString(), mac, stream);
        })).ConfigureAwait(false);

        return new CameraRouteProbe(target.Host, ip.ToString(), target.Port, routed?.Info.Name, routedLocal?.ToString(), probes);
    }

    private static CameraRouteProbe FillFromCache(CameraRouteProbe camera, IReadOnlyList<ArpEntry> arp)
    {
        if (camera.Ip is null) return camera;
        var filled = camera.Candidates
            .Select(c => c.ArpMac is not null
                ? c
                : c with
                {
                    ArpMac = arp.FirstOrDefault(e => e.Ip == camera.Ip
                        && (e.IfIndex == c.InterfaceIndex || e.IfName == c.InterfaceName))?.Mac,
                })
            .ToList();
        return camera with { Candidates = filled };
    }

    // Active (Windows): ask every OTHER adapter in the same subnet who holds our
    // address. Our own MAC coming back is the stack answering for itself.
    private static async Task<IReadOnlyList<AddressConflict>> AskForOwnAddressesAsync(
        IReadOnlyList<Nic> nics, HashSet<string> ownMacs, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<AddressConflict>();
        var owners = Owners(nics);
        var asks = owners
            .SelectMany(own => owners
                .Where(other => !ReferenceEquals(other.Nic, own.Nic) && SameSubnet(other.Address, own.Address, other.Prefix))
                .Select(other => (Own: own, Other: other)))
            .ToList();
        var answers = await Task.WhenAll(asks.Select(a =>
            Task.Run(() => (a.Own, a.Other, Mac: OperatingSystem.IsWindows() ? SendArp(a.Own.Address, a.Other.Address) : null), ct)))
            .ConfigureAwait(false);
        return answers
            .Where(x => x.Mac is not null && !ownMacs.Contains(x.Mac))
            .Select(x => new AddressConflict(x.Own.Address.ToString(), x.Own.Nic.Info.Name, x.Other.Nic.Info.Name, x.Mac!))
            .ToList();
    }

    // Passive: a cache entry for one of our own addresses with a foreign MAC,
    // merged with what the active ask found.
    private static IReadOnlyList<AddressConflict> PassiveConflicts(
        IReadOnlyList<Nic> nics, HashSet<string> ownMacs, IReadOnlyList<ArpEntry> arp, IReadOnlyList<AddressConflict> active)
    {
        var found = active.ToDictionary(c => (c.Address, c.ForeignMac), c => c);
        var owners = Owners(nics);
        foreach (var e in arp)
        {
            var owner = owners.FirstOrDefault(o => o.Address.ToString() == e.Ip);
            if (owner.Nic is null || ownMacs.Contains(e.Mac) || found.ContainsKey((e.Ip, e.Mac))) continue;
            var seenOn = nics.FirstOrDefault(n => n.Info.Index == e.IfIndex || n.Info.Name == e.IfName)?.Info.Name ?? e.IfName ?? "?";
            found[(e.Ip, e.Mac)] = new AddressConflict(e.Ip, owner.Nic.Info.Name, seenOn, e.Mac);
        }
        return found.Values.ToList();
    }

    private static List<(Nic Nic, IPAddress Address, int Prefix)> Owners(IReadOnlyList<Nic> nics) =>
        nics.SelectMany(n => n.V4.Select(a => (Nic: n, a.Address, a.Prefix))).ToList();

    // Host routes (/32) in the active route table: destination IP -> adapter name.
    // The active table is what routes packets right now; whether the route also
    // survives a reboot is checked by the fix script itself.
    private Dictionary<string, string> ReadHostRoutes()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_routes is null) return map;
        try
        {
            foreach (var r in _routes.Read())
            {
                if (r.PrefixLength == 32 && !map.ContainsKey(r.Destination))
                    map[r.Destination] = r.InterfaceName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading host routes failed");
        }
        return map;
    }

    // --- Local adapters ---------------------------------------------------------------

    private (IReadOnlyList<Nic> Nics, HashSet<string> OwnMacs) ReadInterfaces()
    {
        var nics = new List<Nic>();
        var ownMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            string? mac = null;
            try
            {
                mac = FormatMac(nic.GetPhysicalAddress().GetAddressBytes());
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // Some mobile sandboxes hide hardware addresses; the adapter still counts.
            }
            if (mac is not null) ownMacs.Add(mac);

            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                var props = nic.GetIPProperties();
                var index = props.GetIPv4Properties()?.Index ?? -1;
                var v4 = new List<(IPAddress Address, int Prefix)>();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local: no camera subnet
                    v4.Add((ua.Address, PrefixOf(ua)));
                }
                if (v4.Count == 0) continue;
                nics.Add(new Nic(
                    new LocalInterfaceInfo(nic.Name, nic.Description, index, mac ?? "",
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                        v4.Select(a => new LocalAddress(a.Address.ToString(), a.Prefix)).ToList()),
                    v4));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                _logger.LogDebug(ex, "Skipping adapter {Name}", nic.Name);
            }
        }
        return (nics, ownMacs);
    }

    private static int PrefixOf(UnicastIPAddressInformation ua)
    {
        try
        {
            if (ua.PrefixLength is > 0 and <= 32) return ua.PrefixLength;
        }
        catch (PlatformNotSupportedException)
        {
        }
        var mask = ua.IPv4Mask?.GetAddressBytes();
        if (mask is null || mask.Length != 4) return 24;
        var bits = 0;
        foreach (var m in mask)
            for (var i = 7; i >= 0; i--)
                if (((m >> i) & 1) == 1) bits++;
        return bits;
    }

    // --- ARP --------------------------------------------------------------------

    private IReadOnlyList<ArpEntry> ReadArpTable(IReadOnlyList<Nic> nics)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindowsArp();
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) return ReadProcArp();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the ARP cache failed");
        }
        _ = nics;
        return Array.Empty<ArpEntry>();
    }

    private static IReadOnlyList<ArpEntry> ReadWindowsArp()
    {
        var size = 0;
        if (GetIpNetTable(IntPtr.Zero, ref size, false) != ErrorInsufficientBuffer || size <= 0)
            return Array.Empty<ArpEntry>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != NoError)
                return Array.Empty<ArpEntry>();
            // MIB_IPNETTABLE { DWORD dwNumEntries; MIB_IPNETROW table[]; }
            // MIB_IPNETROW { DWORD dwIndex; DWORD dwPhysAddrLen; BYTE bPhysAddr[8]; DWORD dwAddr; DWORD dwType; } = 24 bytes
            var count = Marshal.ReadInt32(buffer);
            var list = new List<ArpEntry>(count);
            var mac = new byte[8];
            var addr = new byte[4];
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + (i * 24));
                var index = Marshal.ReadInt32(row);
                var len = Marshal.ReadInt32(row, 4);
                Marshal.Copy(IntPtr.Add(row, 8), mac, 0, 8);
                Marshal.Copy(IntPtr.Add(row, 16), addr, 0, 4);
                var type = Marshal.ReadInt32(row, 20);
                if (type is not (3 or 4) || len < 6) continue; // dynamic / static only
                var formatted = FormatMac(mac.Take(6).ToArray());
                if (formatted is null) continue;
                list.Add(new ArpEntry(new IPAddress(addr).ToString(), index, null, formatted));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // /proc/net/arp: "IP address  HW type  Flags  HW address  Mask  Device"; flag 0x2 = complete.
    private static IReadOnlyList<ArpEntry> ReadProcArp()
    {
        const string path = "/proc/net/arp";
        if (!File.Exists(path)) return Array.Empty<ArpEntry>();
        var list = new List<ArpEntry>();
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 6) continue;
            if (!int.TryParse(p[2].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) || (flags & 0x2) == 0)
                continue;
            var bytes = p[3].Split(':').Select(h => byte.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : (byte)0).ToArray();
            var mac = bytes.Length == 6 ? FormatMac(bytes) : null;
            if (mac is null) continue;
            list.Add(new ArpEntry(p[0], null, p[5], mac));
        }
        return list;
    }

    private static string? SendArp(IPAddress destination, IPAddress source)
    {
        var mac = new byte[8];
        var len = (uint)mac.Length;
        var result = SendARP(ToIpAddr(destination), ToIpAddr(source), mac, ref len);
        if (result != NoError || len < 6) return null;
        return FormatMac(mac.Take(6).ToArray());
    }

    private static uint ToIpAddr(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

    // "FC-9D-05-50-BF-7D"; null for empty, all-zero, broadcast and multicast addresses.
    private static string? FormatMac(byte[] bytes)
    {
        if (bytes.Length != 6) return null;
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF) || (bytes[0] & 0x01) == 1) return null;
        return string.Join("-", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    // --- Sockets ------------------------------------------------------------------

    // True = connected, false = no answer / refused. A null local address uses the OS route.
    private static async Task<bool?> ConnectAsync(IPAddress? local, IPAddress remote, int port, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            if (local is not null)
                socket.Bind(new IPEndPoint(local, 0));
            await socket.ConnectAsync(new IPEndPoint(remote, port), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
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

    private static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        if (prefix <= 0 || prefix > 32) return false;
        var mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        return (ToHostOrder(a) & mask) == (ToHostOrder(b) & mask);
    }

    private static uint ToHostOrder(IPAddress ip)
    {
        var x = ip.GetAddressBytes();
        return ((uint)x[0] << 24) | ((uint)x[1] << 16) | ((uint)x[2] << 8) | x[3];
    }

    private const int NoError = 0;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    private sealed record Nic(LocalInterfaceInfo Info, List<(IPAddress Address, int Prefix)> V4);

    private sealed record ArpEntry(string Ip, int? IfIndex, string? IfName, string Mac);
}
