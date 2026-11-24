using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.LinkHealth;

// Where this computer sends a camera's packets, and where the camera actually
// answers. The trap this catches: two adapters in one subnet (Wi-Fi from the
// home router and the camera Ethernet port both on 192.168.1.0/24). The OS
// routes the whole subnet out of the adapter with the better metric, so a
// camera on the cable is looked for on Wi-Fi and "drops" — depending on which
// adapter wins after a reboot, the cameras work or don't.

/// <summary>A camera to locate: its host and stream port.</summary>
public sealed record RouteTarget(string Host, int Port);

/// <summary>One IPv4 address of a local adapter.</summary>
public sealed record LocalAddress(string Address, int PrefixLength);

/// <summary>A local network adapter that is up and has IPv4.</summary>
public sealed record LocalInterfaceInfo(
    string Name,
    string Description,
    int Index,
    string Mac,
    bool IsWireless,
    IReadOnlyList<LocalAddress> Addresses);

/// <summary>What one adapter sees of one camera.</summary>
public sealed record InterfaceProbe(
    string InterfaceName,
    int InterfaceIndex,
    string LocalAddress,
    // MAC that answered ARP for the camera's IP on this adapter (null = no answer / unknown).
    string? ArpMac,
    // TCP connect to the stream port with the socket bound to this adapter's
    // address. Null where the OS doesn't send by source address (weak host model).
    bool? StreamReachable);

/// <summary>Probe of one camera across every adapter that shares its subnet.</summary>
public sealed record CameraRouteProbe(
    string Host,
    string? Ip,
    int Port,
    // The adapter the OS picks for this destination right now.
    string? RoutedInterface,
    string? RoutedLocalAddress,
    IReadOnlyList<InterfaceProbe> Candidates,
    // Adapter of a host route (/32) to the camera in the routing table — the
    // fix pins one — or null when the camera rides on the shared subnet route.
    string? PinnedInterface = null);

/// <summary>Another device answers for one of this computer's own addresses.</summary>
public sealed record AddressConflict(string Address, string OwnInterface, string SeenOnInterface, string ForeignMac);

public sealed record HostRoutingSnapshot(
    IReadOnlyList<LocalInterfaceInfo> Interfaces,
    IReadOnlyList<CameraRouteProbe> Cameras,
    IReadOnlyList<AddressConflict> Conflicts);

/// <summary>How sure the analyzer is about the adapter a camera is on.</summary>
public enum PathEvidence
{
    None = 0,
    // Not seen directly; other cameras in its subnet are on that adapter.
    Neighbors,
    // Answered ARP on that adapter.
    Arp,
    // Its stream port answered through that adapter.
    Stream,
}

/// <summary>The analyzer's view of one camera's path.</summary>
public sealed record CameraPathVerdict(
    string Host,
    string? Ip,
    string? RoutedVia,
    string? LivesOn,
    int? LivesOnIndex,
    string? LivesOnLocalAddress,
    PathEvidence Evidence,
    IReadOnlyList<string> SubnetInterfaces,
    // Packets leave by an adapter the camera is not on.
    bool WrongInterface,
    // Two or more adapters share the camera's subnet — works only while the
    // right one wins the route.
    bool SharedSubnet,
    // The camera's IP answers on more than one network (two devices, one IP).
    bool IpOnSeveralNetworks,
    // No adapter got an answer from it.
    bool NotFound,
    // The ARP state per adapter, for display: adapter -> MAC or null.
    IReadOnlyList<InterfaceProbe> PerInterface,
    // Adapter of a host route to the camera, if one exists.
    string? PinnedTo = null,
    // A host route pins the camera to the adapter it is on: the shared subnet
    // no longer matters (a /32 beats the /24 whatever the adapter metrics are,
    // after a reboot too when the route is persistent).
    bool Pinned = false);

/// <summary>One route the fix pins: camera IP → adapter.</summary>
public sealed record RouteFixEntry(
    string CameraHost,
    string CameraIp,
    string InterfaceName,
    int InterfaceIndex,
    PathEvidence Evidence);

public sealed record RouteFixPlan(
    IReadOnlyList<RouteFixEntry> Routes,
    IReadOnlyList<AddressConflict> Conflicts)
{
    public bool IsEmpty => Routes.Count == 0 && Conflicts.Count == 0;
}

/// <summary>Result of a fleet-wide routing check.</summary>
public sealed record RoutingReport(
    DateTimeOffset At,
    HostRoutingSnapshot Snapshot,
    IReadOnlyList<CameraPathVerdict> Verdicts,
    RouteFixPlan Fix);

/// <summary>Locates cameras on this computer's adapters (ARP, bound connects, route choice).</summary>
public interface IHostRoutingProbe
{
    Task<HostRoutingSnapshot> ProbeAsync(IReadOnlyList<RouteTarget> targets, CancellationToken ct);
}
