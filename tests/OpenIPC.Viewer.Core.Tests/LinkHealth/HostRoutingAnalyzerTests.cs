using OpenIPC.Viewer.Core.LinkHealth;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.LinkHealth;

// The scenario these tests are built on is a real one: Wi-Fi (home router,
// 192.168.1.129) and the camera Ethernet port (192.168.1.240) are both on
// 192.168.1.0/24; Wi-Fi has the better metric, so a camera that is not in the
// Ethernet ARP cache yet is looked for on Wi-Fi.
public sealed class HostRoutingAnalyzerTests
{
    private const string Eth = "Ethernet";
    private const string Wifi = "Wi-Fi";

    private static InterfaceProbe EthProbe(string? mac, bool? stream) => new(Eth, 8, "192.168.1.240", mac, stream);
    private static InterfaceProbe WifiProbe(string? mac, bool? stream) => new(Wifi, 6, "192.168.1.129", mac, stream);

    private static CameraRouteProbe Camera(string ip, string routed, params InterfaceProbe[] candidates) =>
        new(ip, ip, 554, routed, routed == Eth ? "192.168.1.240" : "192.168.1.129", candidates);

    private static HostRoutingSnapshot Snapshot(params CameraRouteProbe[] cameras) =>
        new(Array.Empty<LocalInterfaceInfo>(), cameras, Array.Empty<AddressConflict>());

    // .130 and .94 answer on Ethernet; .141 answers nowhere and is routed to Wi-Fi.
    private static HostRoutingSnapshot TheUsersNetwork() => Snapshot(
        Camera("192.168.1.130", Eth, EthProbe("5A-5A-02-30-88-87", true), WifiProbe(null, false)),
        Camera("192.168.1.94", Eth, EthProbe("5A-5A-02-63-86-AD", true), WifiProbe(null, false)),
        Camera("192.168.1.141", Wifi, EthProbe(null, false), WifiProbe(null, false)));

    [Fact]
    public void CameraAnsweringOnEthernet_RoutedThere_IsFragileButNotWrong()
    {
        var v = HostRoutingAnalyzer.Analyze(TheUsersNetwork()).Single(x => x.Ip == "192.168.1.130");
        Assert.Equal(Eth, v.LivesOn);
        Assert.Equal(PathEvidence.Stream, v.Evidence);
        Assert.False(v.WrongInterface);
        Assert.True(v.SharedSubnet);
        Assert.False(v.NotFound);
    }

    [Fact]
    public void SilentCamera_IsPlacedWithItsNeighbours_AndFlaggedAsMisrouted()
    {
        var v = HostRoutingAnalyzer.Analyze(TheUsersNetwork()).Single(x => x.Ip == "192.168.1.141");
        Assert.Equal(Eth, v.LivesOn);
        Assert.Equal(8, v.LivesOnIndex);
        Assert.Equal("192.168.1.240", v.LivesOnLocalAddress);
        Assert.Equal(PathEvidence.Neighbors, v.Evidence);
        Assert.Equal(Wifi, v.RoutedVia);
        Assert.True(v.WrongInterface);
        Assert.True(v.NotFound);
    }

    [Fact]
    public void Plan_PinsEveryCameraOfTheSharedSubnet()
    {
        var snapshot = TheUsersNetwork();
        var plan = HostRoutingAnalyzer.Plan(HostRoutingAnalyzer.Analyze(snapshot), snapshot.Conflicts);
        Assert.Equal(new[] { "192.168.1.94", "192.168.1.130", "192.168.1.141" }, plan.Routes.Select(r => r.CameraIp));
        Assert.All(plan.Routes, r => Assert.Equal(Eth, r.InterfaceName));
        Assert.All(plan.Routes, r => Assert.Equal(8, r.InterfaceIndex));
    }

    [Fact]
    public void StreamAnswerBeatsArpFromAnotherNetwork()
    {
        // A home device holds the same IP on Wi-Fi and answers ARP there; the
        // camera's stream port answers only through Ethernet.
        var snapshot = Snapshot(Camera("192.168.1.50", Wifi,
            EthProbe("5A-5A-00-00-00-01", true), WifiProbe("AA-BB-CC-DD-EE-FF", false)));
        var v = HostRoutingAnalyzer.Analyze(snapshot).Single();
        Assert.Equal(Eth, v.LivesOn);
        Assert.True(v.WrongInterface);
        Assert.True(v.IpOnSeveralNetworks);
    }

    [Fact]
    public void ArpOnlyEvidence_IsUsedWhenThePortIsClosed()
    {
        var v = HostRoutingAnalyzer.Analyze(Snapshot(Camera("192.168.1.60", Wifi,
            EthProbe("5A-5A-00-00-00-02", false), WifiProbe(null, false)))).Single();
        Assert.Equal(Eth, v.LivesOn);
        Assert.Equal(PathEvidence.Arp, v.Evidence);
        Assert.True(v.WrongInterface);
        Assert.False(v.NotFound);
    }

    [Fact]
    public void SingleAdapterSubnet_RoutedRight_NeedsNoRoute()
    {
        var snapshot = Snapshot(Camera("10.0.0.5", Eth, EthProbe("5A-5A-00-00-00-03", true)));
        var verdicts = HostRoutingAnalyzer.Analyze(snapshot);
        var v = verdicts.Single();
        Assert.False(v.SharedSubnet);
        Assert.False(v.WrongInterface);
        Assert.Empty(HostRoutingAnalyzer.Plan(verdicts, snapshot.Conflicts).Routes);
    }

    [Fact]
    public void SilentCameraWithoutNeighbours_IsNotGuessed()
    {
        var snapshot = Snapshot(Camera("192.168.1.141", Wifi, EthProbe(null, false), WifiProbe(null, false)));
        var verdicts = HostRoutingAnalyzer.Analyze(snapshot);
        var v = verdicts.Single();
        Assert.Null(v.LivesOn);
        Assert.False(v.WrongInterface);
        Assert.True(v.NotFound);
        Assert.Empty(HostRoutingAnalyzer.Plan(verdicts, snapshot.Conflicts).Routes);
    }

    [Fact]
    public void UnknownPerAdapterResults_AreNotNotFound()
    {
        // Weak-host OS: only the routed adapter could be tried.
        var v = HostRoutingAnalyzer.Analyze(Snapshot(Camera("192.168.1.70", Wifi,
            EthProbe(null, null), WifiProbe(null, false)))).Single();
        Assert.False(v.NotFound);
    }

    // After the fix ran: every camera has a /32 on Ethernet.
    private static HostRoutingSnapshot AfterTheFix() => Snapshot(
        Camera("192.168.1.130", Eth, EthProbe("5A-5A-02-30-88-87", true), WifiProbe(null, false)) with { PinnedInterface = Eth },
        Camera("192.168.1.94", Eth, EthProbe("5A-5A-02-63-86-AD", true), WifiProbe(null, false)) with { PinnedInterface = Eth },
        Camera("192.168.1.141", Eth, EthProbe("5A-5A-02-64-CE-F5", true), WifiProbe(null, false)) with { PinnedInterface = Eth });

    [Fact]
    public void PinnedCamera_IsNotFragile_AndNeedsNoFix()
    {
        // Regression: the shared subnet kept being reported as "after a reboot
        // packets can go the wrong way" right after the fix pinned every camera.
        var snapshot = AfterTheFix();
        var verdicts = HostRoutingAnalyzer.Analyze(snapshot);
        Assert.All(verdicts, v =>
        {
            Assert.True(v.Pinned);
            Assert.True(v.SharedSubnet);
            Assert.False(v.WrongInterface);
        });
        Assert.Empty(HostRoutingAnalyzer.Plan(verdicts, snapshot.Conflicts).Routes);

        var input = new LinkAnalysisInput(
            DateTimeOffset.Now, true, 554, new PingStats(32, 20, 20, 0.3, 0.5, 1, 0.1), null, null, null,
            null, null, ConnectionCounters.Empty, verdicts[0], null);
        var f = LinkHealthAnalyzer.Analyze(input);
        Assert.Contains(f, x => x.Code == "RoutePinned" && x.Severity == FindingSeverity.Ok);
        Assert.DoesNotContain(f, x => x.Code == "RouteSharedSubnet");
    }

    [Fact]
    public void PinnedToTheWrongAdapter_IsRePinned()
    {
        // A /32 on Wi-Fi forces the camera's packets out of Wi-Fi.
        var snapshot = Snapshot(
            Camera("192.168.1.130", Wifi, EthProbe("5A-5A-02-30-88-87", true), WifiProbe(null, false)) with { PinnedInterface = Wifi });
        var verdicts = HostRoutingAnalyzer.Analyze(snapshot);
        var v = verdicts.Single();
        Assert.False(v.Pinned);
        Assert.True(v.WrongInterface);
        var route = Assert.Single(HostRoutingAnalyzer.Plan(verdicts, snapshot.Conflicts).Routes);
        Assert.Equal(Eth, route.InterfaceName);
    }

    [Fact]
    public void AddressConflict_IsANoteNotAProblem()
    {
        var path = HostRoutingAnalyzer.Analyze(AfterTheFix()).First();
        var conflict = new AddressConflict("192.168.1.240", Eth, Wifi, "9C-97-1B-56-2A-C0");
        var input = new LinkAnalysisInput(
            DateTimeOffset.Now, true, 554, new PingStats(32, 20, 20, 0.3, 0.5, 1, 0.1), null, null, null,
            null, null, ConnectionCounters.Empty, path, new[] { conflict });
        var c = Assert.Single(LinkHealthAnalyzer.Analyze(input), x => x.Code == "AddressConflict");
        Assert.Equal(FindingSeverity.Info, c.Severity);
    }

    [Fact]
    public void LinkFindings_ReportTheMisrouteAndTheConflict()
    {
        var snapshot = TheUsersNetwork();
        var path = HostRoutingAnalyzer.Analyze(snapshot).Single(x => x.Ip == "192.168.1.141");
        var conflict = new AddressConflict("192.168.1.240", Eth, Wifi, "9C-97-1B-56-2A-C0");
        var input = new LinkAnalysisInput(
            DateTimeOffset.Now, false, 554, new PingStats(32, 20, 0, null, null, null, null), null, null, null,
            null, null, ConnectionCounters.Empty, path, new[] { conflict });

        var f = LinkHealthAnalyzer.Analyze(input);
        var wrong = Assert.Single(f, x => x.Code == "RouteWrongInterfaceInferred");
        Assert.Equal(FindingSeverity.Critical, wrong.Severity);
        Assert.Equal(new[] { Wifi, Eth }, wrong.Args);
        Assert.Contains(f, x => x.Code == "RouteNotFound");
        Assert.Contains(f, x => x.Code == "AddressConflict" && x.Args[2] == "9C-97-1B-56-2A-C0");
    }
}
