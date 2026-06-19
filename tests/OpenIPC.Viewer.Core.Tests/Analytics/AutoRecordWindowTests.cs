using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Core.Tests.Analytics;

// Smart auto-stop window (Phase 15.6): start on first detection, extend on each
// new one, stop after a quiet period, cooldown before restarting.
public sealed class AutoRecordWindowTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstDetection_StartsRecording()
    {
        var w = new AutoRecordWindow(postEventSeconds: 15);
        Assert.Equal(AutoRecordAction.Start, w.OnDetection(T0));
        Assert.True(w.IsActive);
    }

    [Fact]
    public void DetectionWhileActive_DoesNotStartAgain()
    {
        var w = new AutoRecordWindow(15);
        w.OnDetection(T0);
        Assert.Equal(AutoRecordAction.None, w.OnDetection(T0.AddSeconds(5)));
    }

    [Fact]
    public void Stops_AfterQuietPeriod()
    {
        var w = new AutoRecordWindow(15);
        w.OnDetection(T0);
        Assert.Equal(AutoRecordAction.None, w.OnTick(T0.AddSeconds(10)));
        Assert.Equal(AutoRecordAction.Stop, w.OnTick(T0.AddSeconds(15)));
        Assert.False(w.IsActive);
    }

    [Fact]
    public void NewDetection_ExtendsTheWindow()
    {
        var w = new AutoRecordWindow(15);
        w.OnDetection(T0);
        w.OnDetection(T0.AddSeconds(10));                          // extend: last = +10s
        Assert.Equal(AutoRecordAction.None, w.OnTick(T0.AddSeconds(20))); // 20-10 = 10 < 15
        Assert.Equal(AutoRecordAction.Stop, w.OnTick(T0.AddSeconds(26))); // 26-10 = 16 >= 15
    }

    [Fact]
    public void Cooldown_BlocksImmediateRestart_ThenAllows()
    {
        var w = new AutoRecordWindow(postEventSeconds: 5, cooldownSeconds: 10);
        w.OnDetection(T0);
        w.OnTick(T0.AddSeconds(5)); // Stop at +5s

        // Within cooldown (10s after stop) → ignored.
        Assert.Equal(AutoRecordAction.None, w.OnDetection(T0.AddSeconds(10)));
        // After cooldown → starts again.
        Assert.Equal(AutoRecordAction.Start, w.OnDetection(T0.AddSeconds(16)));
    }
}
