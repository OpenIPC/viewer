using OpenIPC.Viewer.Core.LinkHealth;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.LinkHealth;

public sealed class LinkHealthAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Own = "192.168.0.11";

    private static PingStats Ping(int sent, int received, int payload = 32, double avg = 0.5, double max = 1, double jitter = 0.2) =>
        new(payload, sent, received, received > 0 ? 0.3 : null, received > 0 ? avg : null, received > 0 ? max : null,
            received > 1 ? jitter : null);

    private static CameraLinkSnapshot Cam(
        int? speed = 100, string duplex = "full", bool? carrier = true, long? upCount = 1,
        long crc = 0, double uptime = 86_400, params CameraTcpConnection[] conns) =>
        new("eth0", "up", carrier, speed, duplex, null, upCount, null, 1_000_000, 1_000_000, crc, 0, crc, 0, 0, 0,
            uptime, false, null, null, conns);

    private static HostLinkSnapshot Host(long speed = 1000, long errors = 0) =>
        new("Ethernet", "NIC", true, speed, Own, errors, 0, 0, 0, 100, 100);

    private static LinkAnalysisInput Input(
        CameraLinkSnapshot? cam = null, bool port = true, PingStats? small = null, PingStats? large = null,
        HostLinkSnapshot? host = null, string? error = null, StressResult? stress = null, ConnectionCounters? counters = null) =>
        new(Now, port, 554, small ?? Ping(20, 20), large ?? Ping(20, 20, 1472), host ?? Host(), cam, error, stress,
            counters ?? ConnectionCounters.Empty);

    private static IReadOnlyList<string> Codes(LinkAnalysisInput i) =>
        LinkHealthAnalyzer.Analyze(i).Select(f => f.Code).ToList();

    [Fact]
    public void HealthyLink_OnlyOkFindings()
    {
        var findings = LinkHealthAnalyzer.Analyze(Input(Cam()));
        Assert.All(findings, f => Assert.True(f.Severity <= FindingSeverity.Info, f.Code));
        Assert.Contains(findings, f => f.Code == "LinkOk");
    }

    [Fact]
    public void NothingAnswers_IsNoResponse()
    {
        var codes = Codes(Input(cam: null, port: false, small: Ping(20, 0), large: Ping(20, 0, 1472), error: "timeout"));
        Assert.Equal("NoResponse", codes[0]);
        Assert.DoesNotContain("CameraProbeUnavailable", codes);
    }

    [Fact]
    public void PingOkButStreamPortClosed_PointsAtStreamer()
    {
        var codes = Codes(Input(Cam(), port: false));
        Assert.Contains("StreamPortClosed", codes);
        Assert.DoesNotContain("NoResponse", codes);
    }

    [Fact]
    public void CameraBootedAfterTheDrop_IsAPowerProblem()
    {
        var counters = ConnectionCounters.Empty with { LastDropAt = Now.AddMinutes(-10), Drops = 1 };
        var f = LinkHealthAnalyzer.Analyze(Input(Cam(uptime: 8 * 60), counters: counters));
        Assert.Equal("RebootedDuringOutage", f[0].Code);
        Assert.Equal(FindingSeverity.Critical, f[0].Severity);
    }

    [Fact]
    public void CameraUpLongerThanTheOutage_PowerHeld()
    {
        var counters = ConnectionCounters.Empty with { LastDropAt = Now.AddMinutes(-10), Drops = 1 };
        Assert.Contains("PowerStable", Codes(Input(Cam(uptime: 3 * 3600), counters: counters)));
    }

    [Fact]
    public void OutageKnownOnlyFromRecovery_IsMatchedToo()
    {
        // App started while the camera was down: no drop, but a recovery with its outage length.
        var counters = ConnectionCounters.Empty with
        {
            LastRecoveredAt = Now.AddMinutes(-1),
            LastOutage = TimeSpan.FromMinutes(5),
        };
        Assert.Contains("RebootedDuringOutage", Codes(Input(Cam(uptime: 4 * 60), counters: counters)));
    }

    [Fact]
    public void TenMegabit_IsSlowLink()
    {
        Assert.Contains("SlowLink", Codes(Input(Cam(speed: 10))));
        Assert.Contains("HalfDuplex", Codes(Input(Cam(duplex: "half"))));
    }

    [Fact]
    public void FlapsAndCrcErrors_AreReported()
    {
        var codes = Codes(Input(Cam(upCount: 4, crc: 50)));
        Assert.Contains("LinkFlaps", codes);
        Assert.Contains("PhysicalErrors", codes);
        Assert.Contains("PhysicalErrorsFew", Codes(Input(Cam(crc: 3))));
    }

    [Fact]
    public void LargePacketsLostMoreThanSmall_IsMarginalCable()
    {
        var codes = Codes(Input(Cam(), small: Ping(20, 20), large: Ping(20, 16, 1472)));
        Assert.Contains("LargePacketLoss", codes);
        Assert.DoesNotContain("PingLoss", codes);
    }

    [Fact]
    public void LeftoverSessionsFromThisComputer_AreFlagged()
    {
        var conns = Enumerable.Range(0, 5)
            .Select(i => new CameraTcpConnection("192.168.0.10", 554, Own, 50000 + i))
            .ToArray();
        var f = LinkHealthAnalyzer.Analyze(Input(Cam(conns: conns)));
        var excess = Assert.Single(f, x => x.Code == "ExcessOwnSessions");
        Assert.Equal("5", excess.Args[0]);
        Assert.Contains(f, x => x.Code == "ManyClients");
    }

    [Fact]
    public void SessionsFromOthers_AreNotOurs()
    {
        var conns = new[]
        {
            new CameraTcpConnection("192.168.0.10", 554, "192.168.0.50", 1),
            new CameraTcpConnection("192.168.0.10", 554, "192.168.0.51", 2),
            new CameraTcpConnection("192.168.0.10", 554, Own, 3),
            new CameraTcpConnection("192.168.0.10", 22, Own, 4),
        };
        var cam = Cam(conns: conns);
        Assert.Equal(1, LinkHealthAnalyzer.OwnRtspSessions(cam, 554, Own));
        Assert.Equal(3, LinkHealthAnalyzer.TotalRtspSessions(cam, 554));
        Assert.DoesNotContain("ExcessOwnSessions", Codes(Input(cam)));
    }

    [Fact]
    public void ErrorsUnderLoad_AreCritical()
    {
        var stress = new StressResult(TimeSpan.FromSeconds(10), Ping(5000, 4990, 1472), 7, 7, 0, 0);
        var f = LinkHealthAnalyzer.Analyze(Input(Cam(), stress: stress));
        Assert.Equal("StressPhysicalErrors", f[0].Code);
        Assert.Equal(FindingSeverity.Critical, f[0].Severity);
        Assert.DoesNotContain(f, x => x.Code == "StressClean");
    }

    [Fact]
    public void CleanStress_SaysSo()
    {
        var stress = new StressResult(TimeSpan.FromSeconds(10), Ping(5000, 5000, 1472), 0, 0, 0, 0);
        Assert.Contains("StressClean", Codes(Input(Cam(), stress: stress)));
    }

    [Fact]
    public void SlowHostAdapter_IsNearEndProblem()
    {
        Assert.Contains("HostSlowLink", Codes(Input(Cam(), host: Host(speed: 10))));
    }

    [Fact]
    public void NoSsh_IsInfoNotProblem()
    {
        var f = LinkHealthAnalyzer.Analyze(Input(cam: null, error: LinkProbeErrors.NoCredentials));
        var probe = Assert.Single(f, x => x.Code == "CameraProbeUnavailable");
        Assert.Equal(FindingSeverity.Info, probe.Severity);
        Assert.Equal(LinkProbeErrors.NoCredentials, probe.Args[0]);
    }

    [Fact]
    public void Findings_AreWorstFirst()
    {
        var f = LinkHealthAnalyzer.Analyze(Input(Cam(speed: 10, upCount: 3), port: false));
        for (var i = 1; i < f.Count; i++)
            Assert.True(f[i - 1].Severity >= f[i].Severity);
    }
}
