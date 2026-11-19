using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenIPC.Viewer.Core.LinkHealth;

// Decides, per camera, which adapter it is really on and whether the OS sends
// its packets there. Evidence ranks: the stream port answering through an
// adapter beats an ARP reply (an ARP reply on the home network can come from
// a different device that happens to hold the same IP), and a camera that
// answers nowhere is placed on the adapter its subnet neighbours are on — a
// camera that is off right now still needs its route pinned so it works the
// moment it comes back.
public static class HostRoutingAnalyzer
{
    public static IReadOnlyList<CameraPathVerdict> Analyze(HostRoutingSnapshot snapshot)
    {
        var direct = snapshot.Cameras.Select(c => (Probe: c, Direct: DirectEvidence(c))).ToList();

        // Where the cameras with direct evidence live, per subnet (a subnet is
        // identified by the set of adapters that share it).
        var homesBySubnet = direct
            .Where(d => d.Direct.Interfaces.Count == 1)
            .GroupBy(d => SubnetKey(d.Probe))
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(d => d.Direct.Interfaces[0])
                      .OrderByDescending(x => x.Count())
                      .ThenBy(x => x.Key, StringComparer.Ordinal)
                      .Select(x => x.Key)
                      .ToList());

        return direct.Select(d => Verdict(d.Probe, d.Direct, homesBySubnet)).ToList();
    }

    /// <summary>
    /// Routes worth pinning: cameras sent the wrong way, or sharing a subnet with
    /// another adapter and not pinned yet. A camera already pinned to the right
    /// adapter needs nothing; one pinned to the wrong adapter is routed the wrong
    /// way and gets re-pinned.
    /// </summary>
    public static RouteFixPlan Plan(IReadOnlyList<CameraPathVerdict> verdicts, IReadOnlyList<AddressConflict> conflicts)
    {
        var routes = verdicts
            .Where(v => v.Ip is not null && v.LivesOn is not null && v.LivesOnIndex is not null
                        && (v.WrongInterface || (v.SharedSubnet && !v.Pinned)))
            .GroupBy(v => v.Ip!)
            .Select(g => g.First())
            .OrderBy(v => IpSortKey(v.Ip!))
            .Select(v => new RouteFixEntry(v.Host, v.Ip!, v.LivesOn!, v.LivesOnIndex!.Value, v.Evidence))
            .ToList();
        return new RouteFixPlan(routes, conflicts);
    }

    private static CameraPathVerdict Verdict(
        CameraRouteProbe probe,
        (IReadOnlyList<string> Interfaces, PathEvidence Evidence) direct,
        Dictionary<string, List<string>> homesBySubnet)
    {
        var subnetIfaces = probe.Candidates.Select(c => c.InterfaceName).Distinct().ToList();
        homesBySubnet.TryGetValue(SubnetKey(probe), out var neighbourHomes);

        string? home = null;
        var evidence = direct.Evidence;
        if (direct.Interfaces.Count == 1)
        {
            home = direct.Interfaces[0];
        }
        else if (direct.Interfaces.Count > 1)
        {
            // Answers on several adapters: the neighbours' adapter, else the routed one.
            home = neighbourHomes?.FirstOrDefault(direct.Interfaces.Contains)
                   ?? (probe.RoutedInterface is { } r && direct.Interfaces.Contains(r) ? r : direct.Interfaces[0]);
        }
        else if (neighbourHomes is { Count: > 0 })
        {
            home = neighbourHomes[0];
            evidence = PathEvidence.Neighbors;
        }

        var homeProbe = home is null ? null : probe.Candidates.FirstOrDefault(c => c.InterfaceName == home);
        var macs = probe.Candidates.Where(c => c.ArpMac is not null).Select(c => c.ArpMac!).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return new CameraPathVerdict(
            Host: probe.Host,
            Ip: probe.Ip,
            RoutedVia: probe.RoutedInterface,
            LivesOn: homeProbe?.InterfaceName,
            LivesOnIndex: homeProbe?.InterfaceIndex,
            LivesOnLocalAddress: homeProbe?.LocalAddress,
            Evidence: homeProbe is null ? PathEvidence.None : evidence,
            SubnetInterfaces: subnetIfaces,
            WrongInterface: homeProbe is not null && probe.RoutedInterface is not null && probe.RoutedInterface != homeProbe.InterfaceName,
            SharedSubnet: subnetIfaces.Count >= 2,
            IpOnSeveralNetworks: macs >= 2,
            // Only when every adapter of its subnet was actually tried (on OSes
            // that can't probe per adapter the others stay null = unknown).
            NotFound: direct.Interfaces.Count == 0 && probe.Ip is not null && probe.Candidates.Count > 0
                      && probe.Candidates.All(c => c.StreamReachable == false),
            PerInterface: probe.Candidates,
            PinnedTo: probe.PinnedInterface,
            Pinned: probe.PinnedInterface is not null && homeProbe is not null
                    && probe.PinnedInterface == homeProbe.InterfaceName);
    }

    private static (IReadOnlyList<string> Interfaces, PathEvidence Evidence) DirectEvidence(CameraRouteProbe probe)
    {
        var stream = probe.Candidates.Where(c => c.StreamReachable == true).Select(c => c.InterfaceName).Distinct().ToList();
        if (stream.Count > 0)
            return (stream, PathEvidence.Stream);
        var arp = probe.Candidates.Where(c => c.ArpMac is not null).Select(c => c.InterfaceName).Distinct().ToList();
        if (arp.Count > 0)
            return (arp, PathEvidence.Arp);
        return (Array.Empty<string>(), PathEvidence.None);
    }

    private static string SubnetKey(CameraRouteProbe probe) =>
        string.Join("|", probe.Candidates.Select(c => c.InterfaceName).Distinct().OrderBy(n => n, StringComparer.Ordinal));

    private static long IpSortKey(string ip)
    {
        long key = 0;
        foreach (var part in ip.Split('.'))
            key = (key << 8) + (int.TryParse(part, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0);
        return key;
    }
}
