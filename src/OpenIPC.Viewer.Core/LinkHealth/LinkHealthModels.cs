using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.LinkHealth;

// Data carried by a link-health check. A check looks at the path between this
// computer and one camera from three vantage points that software can actually
// see: ICMP round trips (the whole path), this computer's network adapter
// counters (the near end), and the camera's own Ethernet PHY/driver counters
// read over SSH (the far end). Per-pin / per-pair electrical state is NOT
// observable from either end — Ethernet PHYs only expose the negotiated result
// (speed/duplex/carrier) and frame-level error counters — so nothing here
// pretends to measure pins.

/// <summary>What started a check.</summary>
public enum LinkCheckTrigger
{
    Manual = 0,
    // Raised after N consecutive failed connect attempts (see ConnectionJournal).
    AfterFailures,
    // Raised once a camera is back after an outage — the moment the camera-side
    // counters (uptime, carrier flaps) tell what the outage was.
    AfterRecovery,
    Stress,
}

/// <summary>Outcome of an ICMP echo series.</summary>
public sealed record PingStats(
    int PayloadBytes,
    int Sent,
    int Received,
    double? MinMs,
    double? AvgMs,
    double? MaxMs,
    // Mean absolute difference between consecutive RTTs (RFC 3550 style, unsmoothed).
    double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>This computer's adapter that routes to the camera.</summary>
public sealed record HostLinkSnapshot(
    string Name,
    string Description,
    bool IsUp,
    long? SpeedMbps,
    string? LocalAddress,
    long? ReceivedErrors,
    long? ReceivedDiscarded,
    long? SentErrors,
    long? SentDiscarded,
    long? ReceivedPackets,
    long? SentPackets);

/// <summary>One established TCP connection seen on the camera.</summary>
public sealed record CameraTcpConnection(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort);

/// <summary>The camera's Ethernet interface as its kernel reports it.</summary>
public sealed record CameraLinkSnapshot(
    string Interface,
    string? OperState,
    bool? Carrier,
    int? SpeedMbps,
    string? Duplex,
    long? CarrierChanges,
    long? CarrierUpCount,
    long? CarrierDownCount,
    long? RxPackets,
    long? TxPackets,
    long? RxErrors,
    long? TxErrors,
    long? RxCrcErrors,
    long? RxFrameErrors,
    long? RxDropped,
    long? Collisions,
    double? UptimeSeconds,
    bool HasEthtool,
    bool? AutoNegotiation,
    string? LinkPartnerModes,
    IReadOnlyList<CameraTcpConnection> Connections)
{
    /// <summary>CRC + frame (alignment) errors — the physical-layer damage signal.</summary>
    public long PhysicalErrors => (RxCrcErrors ?? 0) + (RxFrameErrors ?? 0);

    /// <summary>
    /// Link drops since the camera booted. carrier_up_count counts every link-up
    /// (the first one at boot included); without it carrier_changes counts both
    /// edges, the first up at boot included.
    /// </summary>
    public long? LinkFlapsSinceBoot
    {
        get
        {
            if (CarrierUpCount is { } up) return Math.Max(0, up - 1);
            if (CarrierChanges is { } ch) return Math.Max(0, (ch - 1) / 2);
            return null;
        }
    }
}

/// <summary>Result of the load phase of a stress check.</summary>
public sealed record StressResult(
    TimeSpan Duration,
    PingStats Load,
    long? CameraPhysicalErrorsDelta,
    long? CameraRxErrorsDelta,
    long? CameraFlapsDelta,
    long? HostReceivedErrorsDelta);

/// <summary>Severity of a finding, lowest first.</summary>
public enum FindingSeverity
{
    Ok = 0,
    Info,
    Warning,
    Critical,
}

/// <summary>
/// One conclusion of the analyzer. <see cref="Code"/> is a stable key the UI
/// localizes (title + hint); <see cref="Args"/> fills its placeholders.
/// </summary>
public sealed record LinkFinding(FindingSeverity Severity, string Code, IReadOnlyList<string> Args)
{
    public static LinkFinding Of(FindingSeverity severity, string code, params string[] args) =>
        new(severity, code, args);
}

/// <summary>Everything one check collected plus its conclusions.</summary>
public sealed record LinkCheckReport(
    CameraId CameraId,
    DateTimeOffset At,
    LinkCheckTrigger Trigger,
    bool StreamPortReachable,
    PingStats? SmallPing,
    PingStats? LargePing,
    HostLinkSnapshot? Host,
    CameraLinkSnapshot? Camera,
    string? CameraProbeError,
    StressResult? Stress,
    IReadOnlyList<LinkFinding> Findings,
    // Which adapter the packets leave by and where the camera answers (null when not probed).
    CameraPathVerdict? Path = null)
{
    public FindingSeverity Worst
    {
        get
        {
            var worst = FindingSeverity.Ok;
            foreach (var f in Findings)
                if (f.Severity > worst) worst = f.Severity;
            return worst;
        }
    }
}

/// <summary>Per-camera counters the journal keeps across app restarts.</summary>
public sealed record ConnectionCounters(
    long Connects,
    long Drops,
    long FailedAttempts,
    int ConsecutiveFailures,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDropAt,
    DateTimeOffset? LastRecoveredAt,
    TimeSpan? LastOutage)
{
    public static ConnectionCounters Empty { get; } = new(0, 0, 0, 0, null, null, null, null);
}

/// <summary>Kinds of entries in the connection journal.</summary>
public enum ConnectionEventKind
{
    Connected = 0,
    Dropped,
    FailedAttempt,
    Recovered,
    AutoCheck,
    AutoFix,
}

/// <summary>One journal entry.</summary>
public sealed record ConnectionEvent(CameraId CameraId, DateTimeOffset At, ConnectionEventKind Kind, string? Detail = null);
