using OpenIPC.Viewer.Core.Status;
using OpenIPC.Viewer.Core.Video;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Status;

// Pure resolver: collapses a live SessionState + a TCP reachability probe into
// one CameraStatus. The load-bearing rule is that a faulted stream is never
// masked by a stale optimistic "reachable".
public sealed class CameraStatusPolicyTests
{
    private static CameraStatus Status(CameraStatusInputs i) => CameraStatusPolicy.Resolve(i).Status;
    private static CameraStatusReason Reason(CameraStatusInputs i) => CameraStatusPolicy.Resolve(i).Reason;

    // --- Live session is authoritative ---------------------------------------

    [Theory]
    [InlineData(SessionState.Playing, CameraStatus.Online)]
    [InlineData(SessionState.Paused, CameraStatus.Online)]
    [InlineData(SessionState.Connecting, CameraStatus.Connecting)]
    [InlineData(SessionState.Reconnecting, CameraStatus.Connecting)]
    public void Session_DrivesStatusWhenAttached(SessionState state, CameraStatus expected)
    {
        Assert.Equal(expected, Status(new CameraStatusInputs(Session: state)));
    }

    [Fact]
    public void FailedSession_IsOffline_WhenPortDoesNotAnswer()
    {
        var r = CameraStatusPolicy.Resolve(new CameraStatusInputs(SessionState.Failed, Reachable: false));
        Assert.Equal(CameraStatus.Offline, r.Status);
        Assert.Equal(CameraStatusReason.StreamError, r.Reason);
    }

    [Fact]
    public void FailedSession_IsAttention_WhenPortStillAnswers()
    {
        var r = CameraStatusPolicy.Resolve(new CameraStatusInputs(SessionState.Failed, Reachable: true));
        Assert.Equal(CameraStatus.Attention, r.Status);
        Assert.Equal(CameraStatusReason.StreamErrorButReachable, r.Reason);
    }

    [Fact]
    public void FailedSession_IsOffline_WhenNeverProbed()
    {
        Assert.Equal(CameraStatus.Offline, Status(new CameraStatusInputs(SessionState.Failed)));
    }

    // The core anti-staleness guarantee: a failed stream wins over a lingering
    // "reachable == true" snapshot — it must never read Online.
    [Fact]
    public void FailedSession_NeverReadsOnline_EvenWithStaleReachable()
    {
        Assert.NotEqual(CameraStatus.Online, Status(new CameraStatusInputs(SessionState.Failed, Reachable: true)));
    }

    // --- Probe fallback (no live session) ------------------------------------

    [Theory]
    [InlineData(true, CameraStatus.Online)]
    [InlineData(false, CameraStatus.Offline)]
    public void Probe_DrivesStatusWhenNoSession(bool reachable, CameraStatus expected)
    {
        Assert.Equal(expected, Status(new CameraStatusInputs(Reachable: reachable)));
    }

    [Fact]
    public void ProbeInFlight_IsConnecting()
    {
        var r = CameraStatusPolicy.Resolve(new CameraStatusInputs(ProbeInFlight: true));
        Assert.Equal(CameraStatus.Connecting, r.Status);
        Assert.Equal(CameraStatusReason.Probing, r.Reason);
    }

    [Fact]
    public void NoSignalAtAll_IsUnknown()
    {
        Assert.Equal(CameraStatus.Unknown, Status(default));
    }

    [Fact]
    public void IdleSession_FallsBackToProbe()
    {
        // A pre-start tile (Idle) with a known-good probe should read Online, not
        // get stuck on the idle session.
        Assert.Equal(CameraStatus.Online, Status(new CameraStatusInputs(SessionState.Idle, Reachable: true)));
    }

    [Fact]
    public void ConnectingSession_OutranksReachableProbe()
    {
        // Mid-connect beats a green probe — the stream isn't up yet.
        Assert.Equal(CameraStatus.Connecting, Status(new CameraStatusInputs(SessionState.Connecting, Reachable: true)));
    }
}
