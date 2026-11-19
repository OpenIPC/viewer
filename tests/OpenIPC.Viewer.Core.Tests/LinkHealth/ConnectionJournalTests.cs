using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.LinkHealth;
using OpenIPC.Viewer.Core.Status;
using OpenIPC.Viewer.Core.Video;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.LinkHealth;

// The journal reads the reconnect wrapper's state stream: one dead attempt is
// "Connecting → Failed → Reconnecting", a drop of a live stream is
// "Playing → Failed → Reconnecting".
public sealed class ConnectionJournalTests
{
    private static readonly CameraId Cam = new(Guid.NewGuid());
    private DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private ConnectionJournal Create() => new(clock: () => _now);

    private static void Attempt(ConnectionJournal j)
    {
        j.Report(Cam, SessionState.Connecting);
        j.Report(Cam, SessionState.Failed);
        j.Report(Cam, SessionState.Reconnecting);
    }

    [Fact]
    public void FirstPlay_CountsOneConnection()
    {
        var j = Create();
        j.Report(Cam, SessionState.Connecting);
        j.Report(Cam, SessionState.Playing);

        var c = j.Get(Cam);
        Assert.Equal(1, c.Connects);
        Assert.Equal(0, c.Drops);
        Assert.False(j.IsInOutage(Cam));
    }

    [Fact]
    public void PauseResume_IsNotANewConnection()
    {
        var j = Create();
        j.Report(Cam, SessionState.Playing);
        j.Report(Cam, SessionState.Paused);
        j.Report(Cam, SessionState.Playing);
        Assert.Equal(1, j.Get(Cam).Connects);
    }

    [Fact]
    public void DropOfLiveStream_CountsDrop_NotFailedAttempt()
    {
        var j = Create();
        j.Report(Cam, SessionState.Playing);
        j.Report(Cam, SessionState.Failed);
        j.Report(Cam, SessionState.Reconnecting);

        var c = j.Get(Cam);
        Assert.Equal(1, c.Drops);
        Assert.Equal(0, c.FailedAttempts);
        Assert.True(j.IsInOutage(Cam));
        Assert.Equal(_now, c.LastDropAt);
    }

    [Fact]
    public void ThirdFailedAttempt_RaisesThresholdOnce()
    {
        var j = Create();
        var raised = new List<int>();
        j.ThresholdReached += (_, e) => raised.Add(e.ConsecutiveFailures);

        Attempt(j);
        Attempt(j);
        Assert.Empty(raised);
        Attempt(j);
        Assert.Equal(new[] { 3 }, raised);

        Attempt(j); // still the same outage — no second check
        Assert.Single(raised);
        Assert.Equal(4, j.Get(Cam).ConsecutiveFailures);
    }

    [Fact]
    public void Recovery_ReportsOutageAndResetsConsecutive()
    {
        var j = Create();
        ConnectionRecoveredEventArgs? rec = null;
        j.Recovered += (_, e) => rec = e;

        j.Report(Cam, SessionState.Playing);
        j.Report(Cam, SessionState.Failed);
        Attempt(j);
        Attempt(j);
        _now = _now.AddSeconds(95);
        j.Report(Cam, SessionState.Connecting);
        j.Report(Cam, SessionState.Playing);

        Assert.NotNull(rec);
        Assert.Equal(TimeSpan.FromSeconds(95), rec!.Outage);
        Assert.Equal(2, rec.FailedAttempts);
        var c = j.Get(Cam);
        Assert.Equal(0, c.ConsecutiveFailures);
        Assert.Equal(2, c.Connects);
        Assert.Equal(TimeSpan.FromSeconds(95), c.LastOutage);
        Assert.False(j.IsInOutage(Cam));
    }

    [Fact]
    public void ThresholdIsArmedAgainAfterRecovery()
    {
        var j = Create();
        var raised = 0;
        j.ThresholdReached += (_, _) => raised++;

        for (var i = 0; i < 3; i++) Attempt(j);
        j.Report(Cam, SessionState.Playing);
        j.Report(Cam, SessionState.Failed);
        for (var i = 0; i < 3; i++) Attempt(j);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void IdleOrTornDownTile_IsNotAnEvent()
    {
        var j = Create();
        j.Report(Cam, SessionState.Playing);
        j.Report(Cam, SessionState.Idle);
        j.Report(Cam, null);

        var c = j.Get(Cam);
        Assert.Equal(1, c.Connects);
        Assert.Equal(0, c.Drops);
        Assert.Equal(0, c.FailedAttempts);
    }

    [Fact]
    public void Registry_FeedsJournal()
    {
        var registry = new CameraStatusRegistry();
        using var j = new ConnectionJournal(registry, () => _now);
        registry.ReportSession(Cam, SessionState.Playing);
        registry.ReportSession(Cam, SessionState.Failed);

        Assert.Equal(1, j.Get(Cam).Drops);
    }

    [Fact]
    public void ExportImport_KeepsCountersButNotTheLiveOutage()
    {
        var j = Create();
        j.Report(Cam, SessionState.Playing);
        j.Report(Cam, SessionState.Failed);
        Attempt(j);
        var (counters, events) = j.Export();

        var restored = Create();
        restored.Import(counters, events);

        var c = restored.Get(Cam);
        Assert.Equal(1, c.Connects);
        Assert.Equal(1, c.Drops);
        Assert.Equal(1, c.FailedAttempts);
        Assert.Equal(0, c.ConsecutiveFailures);
        Assert.False(restored.IsInOutage(Cam));
        Assert.Equal(events.Count, restored.Events().Count);
    }

    [Fact]
    public void Reset_ClearsOneCamera()
    {
        var j = Create();
        var other = new CameraId(Guid.NewGuid());
        j.Report(Cam, SessionState.Playing);
        j.Report(other, SessionState.Playing);
        j.Reset(Cam);

        Assert.Equal(0, j.Get(Cam).Connects);
        Assert.Equal(1, j.Get(other).Connects);
        Assert.DoesNotContain(j.Events(), e => e.CameraId == Cam);
    }
}
