using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>Inputs of one analysis, gathered by <see cref="LinkHealthService"/>.</summary>
public sealed record LinkAnalysisInput(
    DateTimeOffset At,
    bool StreamPortReachable,
    int StreamPort,
    PingStats? SmallPing,
    PingStats? LargePing,
    HostLinkSnapshot? Host,
    CameraLinkSnapshot? Camera,
    string? CameraProbeError,
    StressResult? Stress,
    ConnectionCounters Counters,
    // Where this computer sends the camera's packets vs. where it answers.
    CameraPathVerdict? Path = null,
    IReadOnlyList<AddressConflict>? Conflicts = null);

// Turns raw measurements into findings. Pure and deterministic so every rule
// is unit-tested. The rules only claim what the data supports: a negotiated
// 10 Mbit/s link or CRC errors say "this link is damaged", a camera uptime
// shorter than the outage says "it lost power", but nothing here can name the
// pin or pair that is at fault.
public static class LinkHealthAnalyzer
{
    // A camera that booted within this margin of an outage's start rebooted
    // because of it (boot takes tens of seconds; clocks differ a little).
    private static readonly TimeSpan BootMargin = TimeSpan.FromSeconds(90);
    // Only outages this recent are matched against the camera's boot time.
    private static readonly TimeSpan OutageMatchWindow = TimeSpan.FromHours(12);
    private const double RecentBootSeconds = 15 * 60;
    // Grid substream + single-view mainstream + a recorder process can each hold
    // an RTSP session from this computer; more than that is a leftover.
    public const int ExpectedOwnRtspSessions = 3;
    private const int ManyClients = 5;

    public static IReadOnlyList<LinkFinding> Analyze(LinkAnalysisInput input)
    {
        var f = new List<LinkFinding>();
        var cam = input.Camera;
        var pingDead = input.SmallPing is { Sent: > 0, Received: 0 };

        // --- Reachability ------------------------------------------------------
        if (pingDead && !input.StreamPortReachable && cam is null)
            f.Add(LinkFinding.Of(FindingSeverity.Critical, "NoResponse"));
        else if (!input.StreamPortReachable && (cam is not null || input.SmallPing is { Received: > 0 }))
            f.Add(LinkFinding.Of(FindingSeverity.Warning, "StreamPortClosed", N(input.StreamPort)));

        if (cam is null && input.CameraProbeError is { } err && !(pingDead && !input.StreamPortReachable))
            f.Add(LinkFinding.Of(FindingSeverity.Info, "CameraProbeUnavailable", err));

        // --- This computer's routing --------------------------------------------
        AnalyzePath(input, f);

        // --- Power (camera uptime vs. the last outage) ---------------------------
        if (cam?.UptimeSeconds is { } uptime)
        {
            var bootedAt = input.At - TimeSpan.FromSeconds(uptime);
            var outageStart = LastOutageStart(input.Counters);
            if (outageStart is { } start && input.At - start < OutageMatchWindow)
            {
                if (bootedAt >= start - BootMargin)
                    f.Add(LinkFinding.Of(FindingSeverity.Critical, "RebootedDuringOutage", Minutes(uptime)));
                else
                    f.Add(LinkFinding.Of(FindingSeverity.Ok, "PowerStable", Minutes(uptime)));
            }
            else if (uptime < RecentBootSeconds)
            {
                f.Add(LinkFinding.Of(FindingSeverity.Info, "RecentBoot", Minutes(uptime)));
            }
        }

        // --- Camera PHY ---------------------------------------------------------------
        if (cam is not null)
        {
            if (cam.Carrier == false || string.Equals(cam.OperState, "down", StringComparison.OrdinalIgnoreCase))
            {
                f.Add(LinkFinding.Of(FindingSeverity.Critical, "CameraLinkDown", cam.Interface));
            }
            else if (cam.SpeedMbps is { } speed)
            {
                var half = string.Equals(cam.Duplex, "half", StringComparison.OrdinalIgnoreCase);
                if (speed < 100)
                    f.Add(LinkFinding.Of(FindingSeverity.Warning, "SlowLink", N(speed), cam.Duplex ?? "?"));
                else if (half)
                    f.Add(LinkFinding.Of(FindingSeverity.Warning, "HalfDuplex", N(speed)));
                else
                    f.Add(LinkFinding.Of(FindingSeverity.Ok, "LinkOk", N(speed), cam.Duplex ?? "?"));
            }

            if (cam.LinkFlapsSinceBoot is { } flaps && flaps > 0)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "LinkFlaps", N(flaps)));

            var phys = cam.PhysicalErrors;
            if (phys >= 10)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "PhysicalErrors", N(phys), Ppm(phys, cam.RxPackets)));
            else if (phys > 0)
                f.Add(LinkFinding.Of(FindingSeverity.Info, "PhysicalErrorsFew", N(phys)));

            AnalyzeConnections(cam, input, f);
        }

        // --- Path (ICMP) ------------------------------------------------------------
        if (input.SmallPing is { Sent: > 0 } small && small.Received > 0)
        {
            if (small.LossPercent >= 1)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "PingLoss", Pct(small.LossPercent)));
            if (input.LargePing is { Sent: > 0 } large && large.LossPercent - small.LossPercent >= 5)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "LargePacketLoss", Pct(small.LossPercent), Pct(large.LossPercent), N(large.PayloadBytes)));
            if (small.MaxMs > 100 || small.JitterMs > 20)
                f.Add(LinkFinding.Of(FindingSeverity.Info, "UnstableLatency", Ms(small.MaxMs), Ms(small.JitterMs)));
        }

        // --- Near end (this computer) ------------------------------------------------
        if (input.Host is { } host)
        {
            if (!host.IsUp)
                f.Add(LinkFinding.Of(FindingSeverity.Critical, "HostLinkDown", host.Name));
            else if (host.SpeedMbps is { } hs && hs > 0 && hs < 100)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "HostSlowLink", host.Name, N(hs)));
            var hostErrors = (host.ReceivedErrors ?? 0) + (host.SentErrors ?? 0);
            if (hostErrors > 0)
                f.Add(LinkFinding.Of(FindingSeverity.Info, "HostErrors", host.Name, N(hostErrors)));
        }

        // --- Stress -----------------------------------------------------------------
        if (input.Stress is { } st)
        {
            if (st.CameraPhysicalErrorsDelta > 0)
                f.Add(LinkFinding.Of(FindingSeverity.Critical, "StressPhysicalErrors", N(st.CameraPhysicalErrorsDelta.Value)));
            if (st.CameraFlapsDelta > 0)
                f.Add(LinkFinding.Of(FindingSeverity.Critical, "StressLinkFlaps", N(st.CameraFlapsDelta.Value)));
            if (st.Load.Sent > 0 && st.Load.LossPercent >= 2)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "StressLoss", Pct(st.Load.LossPercent)));
            if (st.HostReceivedErrorsDelta > 0)
                f.Add(LinkFinding.Of(FindingSeverity.Warning, "StressHostErrors", N(st.HostReceivedErrorsDelta.Value)));
            if (f.All(x => x.Severity < FindingSeverity.Warning))
                f.Add(LinkFinding.Of(FindingSeverity.Ok, "StressClean", Pct(st.Load.LossPercent)));
        }

        // --- History ----------------------------------------------------------------
        if (input.Counters.Drops >= 3)
            f.Add(LinkFinding.Of(FindingSeverity.Info, "FrequentDrops", N(input.Counters.Drops)));

        return f.OrderByDescending(x => x.Severity).ToList();
    }

    /// <summary>RTSP sessions on the camera that come from this computer.</summary>
    public static int OwnRtspSessions(CameraLinkSnapshot camera, int rtspPort, string? ownAddress) =>
        ownAddress is null
            ? 0
            : camera.Connections.Count(c => c.LocalPort == rtspPort && c.RemoteAddress == ownAddress);

    public static int TotalRtspSessions(CameraLinkSnapshot camera, int rtspPort) =>
        camera.Connections.Count(c => c.LocalPort == rtspPort);

    /// <summary>The start of the most recent outage the journal knows about.</summary>
    public static DateTimeOffset? LastOutageStart(ConnectionCounters c)
    {
        DateTimeOffset? fromRecovery = c.LastRecoveredAt is { } rec && c.LastOutage is { } dur ? rec - dur : null;
        if (c.LastDropAt is { } drop)
            return fromRecovery is { } r && r > drop ? r : drop;
        return fromRecovery;
    }

    private static void AnalyzePath(LinkAnalysisInput input, List<LinkFinding> f)
    {
        if (input.Path is not { } p) return;

        if (p.WrongInterface)
        {
            f.Add(LinkFinding.Of(FindingSeverity.Critical,
                p.Evidence == PathEvidence.Neighbors ? "RouteWrongInterfaceInferred" : "RouteWrongInterface",
                p.RoutedVia ?? "?", p.LivesOn ?? "?"));
        }
        else if (p.Pinned && p.LivesOn is not null)
        {
            // The shared subnet no longer matters: a host route holds the camera on its adapter.
            f.Add(LinkFinding.Of(FindingSeverity.Ok, "RoutePinned", p.LivesOn));
        }
        else if (p.SharedSubnet && p.LivesOn is not null)
        {
            f.Add(LinkFinding.Of(FindingSeverity.Warning, "RouteSharedSubnet",
                string.Join(", ", p.SubnetInterfaces), p.LivesOn));
        }

        if (p.IpOnSeveralNetworks)
            f.Add(LinkFinding.Of(FindingSeverity.Warning, "RouteIpOnSeveralNetworks",
                p.Ip ?? p.Host, string.Join(", ", p.PerInterface.Where(x => x.ArpMac is not null).Select(x => x.InterfaceName))));

        if (p.NotFound)
            f.Add(LinkFinding.Of(FindingSeverity.Info, "RouteNotFound", string.Join(", ", p.SubnetInterfaces)));

        // Our own address on the camera's network is used by a device on the
        // OTHER network. While the two networks stay apart nobody actually
        // collides (each adapter only talks on its own wire), so this is a note
        // about a latent clash — it becomes real only if the networks are joined.
        foreach (var c in input.Conflicts ?? Array.Empty<AddressConflict>())
        {
            if (c.OwnInterface == p.LivesOn || c.OwnInterface == p.RoutedVia)
                f.Add(LinkFinding.Of(FindingSeverity.Info, "AddressConflict", c.Address, c.OwnInterface, c.ForeignMac, c.SeenOnInterface));
        }
    }

    private static void AnalyzeConnections(CameraLinkSnapshot cam, LinkAnalysisInput input, List<LinkFinding> f)
    {
        var own = OwnRtspSessions(cam, input.StreamPort, input.Host?.LocalAddress);
        var total = TotalRtspSessions(cam, input.StreamPort);
        if (own > ExpectedOwnRtspSessions)
            f.Add(LinkFinding.Of(FindingSeverity.Warning, "ExcessOwnSessions", N(own), N(ExpectedOwnRtspSessions)));
        if (total >= ManyClients)
            f.Add(LinkFinding.Of(FindingSeverity.Info, "ManyClients", N(total), N(total - own)));
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Ms(double? v) => v is { } x ? x.ToString("0.#", CultureInfo.InvariantCulture) : "?";
    private static string Minutes(double seconds) => ((long)(seconds / 60)).ToString(CultureInfo.InvariantCulture);

    private static string Ppm(long errors, long? packets) =>
        packets is { } p && p > 0
            ? (1_000_000.0 * errors / p).ToString("0.#", CultureInfo.InvariantCulture)
            : "?";
}
