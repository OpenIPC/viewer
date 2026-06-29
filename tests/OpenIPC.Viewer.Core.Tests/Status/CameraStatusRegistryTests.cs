using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Status;
using OpenIPC.Viewer.Core.Video;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Status;

// The merge point: session + reachability from independent sources collapse into
// one verdict, and Changed only fires on a real move.
public sealed class CameraStatusRegistryTests
{
    private static CameraId NewId() => new(Guid.NewGuid());

    [Fact]
    public void Get_IsUnknown_BeforeAnyReport()
    {
        var reg = new CameraStatusRegistry();
        Assert.Equal(CameraStatus.Unknown, reg.Get(NewId()).Status);
    }

    [Fact]
    public void ReportSession_DrivesStatus()
    {
        var reg = new CameraStatusRegistry();
        var id = NewId();
        reg.ReportSession(id, SessionState.Playing);
        Assert.Equal(CameraStatus.Online, reg.Get(id).Status);
    }

    [Fact]
    public void SessionAndProbe_MergeIntoAttention()
    {
        // A grid tile sees the stream fault; a probe (from the library) says the
        // port still answers — the merged verdict is Attention, visible to both.
        var reg = new CameraStatusRegistry();
        var id = NewId();
        reg.ReportReachability(id, reachable: true);
        reg.ReportSession(id, SessionState.Failed);
        Assert.Equal(CameraStatus.Attention, reg.Get(id).Status);
    }

    [Fact]
    public void ReportSessionNull_ClearsSessionSignal_FallsBackToProbe()
    {
        var reg = new CameraStatusRegistry();
        var id = NewId();
        reg.ReportReachability(id, reachable: true);
        reg.ReportSession(id, SessionState.Failed); // Attention
        reg.ReportSession(id, null);                 // tile torn down
        Assert.Equal(CameraStatus.Online, reg.Get(id).Status); // probe alone
    }

    [Fact]
    public void Changed_FiresOnlyOnRealMove()
    {
        var reg = new CameraStatusRegistry();
        var id = NewId();
        var snapshots = new List<CameraStatusSnapshot>();
        reg.Changed += (_, s) => snapshots.Add(s);

        reg.ReportSession(id, SessionState.Playing); // Unknown -> Online (fire)
        reg.ReportSession(id, SessionState.Playing); // no-op (Playing again)
        reg.ReportSession(id, SessionState.Paused);  // still Online (no-op verdict)
        reg.ReportSession(id, SessionState.Failed);  // Online -> Offline (fire)

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(CameraStatus.Online, snapshots[0].Result.Status);
        Assert.Equal(CameraStatus.Offline, snapshots[1].Result.Status);
        Assert.Equal(id, snapshots[1].CameraId);
    }

    [Fact]
    public void Reachability_DrivesStatus_WithNoSession()
    {
        var reg = new CameraStatusRegistry();
        var id = NewId();
        reg.ReportReachability(id, reachable: false);
        Assert.Equal(CameraStatus.Offline, reg.Get(id).Status);

        reg.ReportReachability(id, reachable: null, probeInFlight: true);
        Assert.Equal(CameraStatus.Connecting, reg.Get(id).Status);
    }
}
