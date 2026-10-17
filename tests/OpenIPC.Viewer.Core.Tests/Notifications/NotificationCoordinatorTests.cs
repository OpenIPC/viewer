using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;
using OpenIPC.Viewer.Core.Settings;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Notifications;

public class NotificationCoordinatorTests
{
    private static readonly DateTime Noon = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData(2, 22, 7, true)]   // 02:00 inside 22→07 wrap window
    [InlineData(23, 22, 7, true)]  // 23:00 inside
    [InlineData(12, 22, 7, false)] // noon outside
    [InlineData(8, 22, 7, false)]  // just after end
    [InlineData(10, 9, 17, true)]  // non-wrap window
    [InlineData(17, 9, 17, false)] // exclusive end
    [InlineData(5, 5, 5, false)]   // empty window
    public void QuietHours_WrapsAcrossMidnight(int hour, int start, int end, bool expected)
        => Assert.Equal(expected, NotificationCoordinator.InQuietHours(hour, start, end));

    [Fact]
    public void Delivers_MotionWhenEnabled()
    {
        var h = Make(new Settings());
        h.Src.Emit(Event(EventKind.Motion));
        Assert.Single(h.Sink.Shown);
    }

    [Fact]
    public void Suppresses_WhenMasterOff()
    {
        var h = Make(new Settings { NotificationsEnabled = false });
        h.Src.Emit(Event(EventKind.Motion));
        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public void Suppresses_DetectionWhenTypeOff()
    {
        var h = Make(new Settings { NotifyOnDetection = false });
        h.Src.Emit(Event(EventKind.Detection));
        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public void Cooldown_SuppressesSecondWithinWindow()
    {
        var now = Noon;
        var h = Make(new Settings { NotificationCooldownSeconds = 30 }, () => now);
        var cam = new CameraId(Guid.NewGuid());

        h.Src.Emit(Event(EventKind.Motion, cam));
        now = now.AddSeconds(10);
        h.Src.Emit(Event(EventKind.Motion, cam));   // within cooldown → dropped
        now = now.AddSeconds(40);
        h.Src.Emit(Event(EventKind.Motion, cam));   // cooldown elapsed → shown

        Assert.Equal(2, h.Sink.Shown.Count);
    }

    [Fact]
    public void Cooldown_IsPerCamera()
    {
        var h = Make(new Settings { NotificationCooldownSeconds = 30 });
        h.Src.Emit(Event(EventKind.Motion, new CameraId(Guid.NewGuid())));
        h.Src.Emit(Event(EventKind.Motion, new CameraId(Guid.NewGuid())));
        Assert.Equal(2, h.Sink.Shown.Count); // different cameras, both fire
    }

    [Fact]
    public void UnavailableSink_NeverSubscribes()
    {
        var src = new Subject();
        var sink = new FakeSink { Available = false };
        using var coord = new NotificationCoordinator(src, sink, new Settings());
        coord.Start();
        src.Emit(Event(EventKind.Motion));
        Assert.Empty(sink.Shown);
    }

    // The ingestion stream publishes one episode several times (open, peak
    // updates, close) — that must stay one notification, even with no cooldown.
    [Fact]
    public void SameEpisode_NotifiesOnce()
    {
        var h = Make(new Settings { NotificationCooldownSeconds = 0 });
        var open = Event(EventKind.Detection, confidence: 0.9f, objects: 1);
        h.Src.Emit(open);
        h.Src.Emit(open with { PeakObjects = 2 });
        h.Src.Emit(open with { EndedAt = open.OccurredAt.AddSeconds(40) });
        Assert.Single(h.Sink.Shown);
    }

    [Fact]
    public void ConfidenceThreshold_WaitsForPeakUpdate()
    {
        var h = Make(new Settings { NotifyMinConfidencePercent = 70 });
        var open = Event(EventKind.Detection, confidence: 0.55f, objects: 1);

        h.Src.Emit(open);
        Assert.Empty(h.Sink.Shown);

        h.Src.Emit(open with { PeakConfidence = 0.82f });
        Assert.Single(h.Sink.Shown);
        Assert.Contains("82%", h.Sink.Shown[0].Body);
    }

    [Fact]
    public void ConfidenceThreshold_ClosedBelow_NeverNotifies()
    {
        var h = Make(new Settings { NotifyMinConfidencePercent = 70 });
        var open = Event(EventKind.Detection, confidence: 0.6f, objects: 1);
        h.Src.Emit(open);
        h.Src.Emit(open with { EndedAt = open.OccurredAt.AddSeconds(3) });
        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public void ConfidenceThreshold_DoesNotApplyToMotion()
    {
        var h = Make(new Settings { NotifyMinConfidencePercent = 90, NotifyMinObjects = 3 });
        h.Src.Emit(Event(EventKind.Motion));
        Assert.Single(h.Sink.Shown);
    }

    [Fact]
    public void ObjectThreshold_NeedsEnoughObjects()
    {
        var h = Make(new Settings { NotifyMinObjects = 3 });
        var open = Event(EventKind.Detection, confidence: 0.9f, objects: 2);
        h.Src.Emit(open);
        Assert.Empty(h.Sink.Shown);
        h.Src.Emit(open with { PeakObjects = 3 });
        Assert.Single(h.Sink.Shown);
    }

    [Fact]
    public void DurationThreshold_NotifiesOnceActivityHasLastedLongEnough()
    {
        var h = Make(new Settings { NotifyMinDurationSeconds = 3 });
        var open = Event(EventKind.Motion, occurredAt: Noon.ToUniversalTime());

        h.Src.Emit(open);
        h.Src.Emit(open with { LastSeenAt = open.OccurredAt.AddSeconds(2) });
        Assert.Empty(h.Sink.Shown);

        h.Src.Emit(open with { LastSeenAt = open.OccurredAt.AddSeconds(3.1) });
        Assert.Single(h.Sink.Shown); // while the episode is still open
    }

    // Regression: the episode stays open for its 5 s quiet window after a single
    // blip; elapsed wall-clock time must not count as "lasted N seconds".
    [Fact]
    public void DurationThreshold_TimePassingAlone_DoesNotCount()
    {
        var now = Noon;
        var h = Make(new Settings { NotifyMinDurationSeconds = 3 }, () => now);
        var open = Event(EventKind.Motion, occurredAt: Noon.ToUniversalTime()) with { LastSeenAt = Noon.ToUniversalTime() };

        h.Src.Emit(open);
        now = Noon.AddSeconds(4);
        h.Src.Emit(open with { EndedAt = open.OccurredAt });

        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public void DurationThreshold_ShortEpisode_NeverNotifies()
    {
        var h = Make(new Settings { NotifyMinDurationSeconds = 5 });
        var open = Event(EventKind.Motion, occurredAt: Noon.ToUniversalTime());

        h.Src.Emit(open);
        h.Src.Emit(open with { EndedAt = open.OccurredAt.AddSeconds(2) });

        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public void DurationThreshold_ClosedLongEnough_NotifiesAtClose()
    {
        var h = Make(new Settings { NotifyMinDurationSeconds = 5 });
        var open = Event(EventKind.Motion, occurredAt: Noon.ToUniversalTime());
        h.Src.Emit(open);
        h.Src.Emit(open with { EndedAt = open.OccurredAt.AddSeconds(8) });
        Assert.Single(h.Sink.Shown);
    }

    [Fact]
    public void Actions_RunWhenEnabled_WithCameraName()
    {
        var actions = new FakeDispatcher();
        var h = Make(new Settings { EventActions = ActionsOn() }, actions: actions,
            cameraName: (_, _) => Task.FromResult<string?>("Gate"));
        h.Src.Emit(Event(EventKind.Detection, confidence: 0.9f, objects: 1));

        var ctx = Assert.Single(actions.Dispatched);
        Assert.Equal("Gate", ctx.CameraName);
        Assert.StartsWith("Gate · ", Assert.Single(h.Sink.Shown).Body);
    }

    [Fact]
    public void Actions_OffByDefault()
    {
        var actions = new FakeDispatcher();
        var h = Make(new Settings(), actions: actions);
        h.Src.Emit(Event(EventKind.Motion));
        Assert.Empty(actions.Dispatched);
    }

    [Fact]
    public void Actions_FireDuringQuietHours_ByDefault()
    {
        var night = new DateTime(2026, 6, 21, 2, 0, 0, DateTimeKind.Local);
        var actions = new FakeDispatcher();
        var h = Make(new Settings { QuietHoursEnabled = true, EventActions = ActionsOn() }, () => night, actions);

        h.Src.Emit(Event(EventKind.Motion));

        Assert.Empty(h.Sink.Shown);          // the toast is silenced…
        Assert.Single(actions.Dispatched);   // …the alarm action is not
    }

    [Fact]
    public void Actions_HonourQuietHours_WhenAsked()
    {
        var night = new DateTime(2026, 6, 21, 2, 0, 0, DateTimeKind.Local);
        var actions = new FakeDispatcher();
        var h = Make(new Settings
        {
            QuietHoursEnabled = true,
            EventActions = ActionsOn() with { IgnoreQuietHours = false },
        }, () => night, actions);

        h.Src.Emit(Event(EventKind.Motion));
        Assert.Empty(actions.Dispatched);
    }

    [Fact]
    public void Actions_RespectThresholds()
    {
        var actions = new FakeDispatcher();
        var h = Make(new Settings { NotifyMinConfidencePercent = 80, EventActions = ActionsOn() }, actions: actions);
        h.Src.Emit(Event(EventKind.Detection, confidence: 0.5f, objects: 1));
        Assert.Empty(actions.Dispatched);
    }

    [Fact]
    public void Actions_HaveTheirOwnCooldown()
    {
        var now = Noon;
        var actions = new FakeDispatcher();
        var cam = new CameraId(Guid.NewGuid());
        var h = Make(new Settings
        {
            NotificationCooldownSeconds = 0,
            EventActions = ActionsOn() with { CooldownSeconds = 60 },
        }, () => now, actions);

        h.Src.Emit(Event(EventKind.Motion, cam));
        now = now.AddSeconds(20);
        h.Src.Emit(Event(EventKind.Motion, cam));

        Assert.Equal(2, h.Sink.Shown.Count);
        Assert.Single(actions.Dispatched);
    }

    [Fact]
    public void Actions_WorkWithoutANativeSink()
    {
        var src = new Subject();
        var sink = new FakeSink { Available = false };
        var actions = new FakeDispatcher();
        using var coord = new NotificationCoordinator(src, sink, new Settings { EventActions = ActionsOn() },
            () => Noon, actions);
        coord.Start();

        src.Emit(Event(EventKind.Motion));

        Assert.Empty(sink.Shown);
        Assert.Single(actions.Dispatched);
    }

    private static EventActionSettings ActionsOn() => new(
        Enabled: true, HttpEnabled: true, HttpUrl: "http://127.0.0.1/hook");

    private sealed record Harness(Subject Src, FakeSink Sink, NotificationCoordinator Coord);

    private static Harness Make(
        Settings s,
        Func<DateTime>? clock = null,
        IEventActionDispatcher? actions = null,
        Func<CameraId, CancellationToken, Task<string?>>? cameraName = null)
    {
        var src = new Subject();
        var sink = new FakeSink();
        var coord = new NotificationCoordinator(src, sink, s, clock ?? (() => Noon), actions, cameraName);
        coord.Start();
        return new Harness(src, sink, coord);
    }

    private static CameraEvent Event(
        EventKind kind, CameraId? cam = null, float? confidence = null, int objects = 0, DateTime? occurredAt = null) => new(
        new EventId(Guid.NewGuid()), cam ?? new CameraId(Guid.NewGuid()), kind,
        EventSeverity.Info, occurredAt ?? Noon.ToUniversalTime(), null, "test", "summary", confidence, objects);

    private sealed class FakeSink : INotificationService
    {
        public bool Available = true;
        public bool IsAvailable => Available;
        public List<NotificationRequest> Shown { get; } = new();
        public void Show(NotificationRequest request) => Shown.Add(request);
    }

    private sealed class FakeDispatcher : IEventActionDispatcher
    {
        public List<EventActionContext> Dispatched { get; } = new();

        public Task DispatchAsync(EventActionContext context, EventActionSettings settings, CancellationToken ct)
        {
            Dispatched.Add(context);
            return Task.CompletedTask;
        }

        public Task TestAsync(EventActionChannel channel, EventActionContext context, EventActionSettings settings, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class Subject : IObservable<CameraEvent>
    {
        private readonly List<IObserver<CameraEvent>> _obs = new();
        public void Emit(CameraEvent e) { foreach (var o in _obs.ToArray()) o.OnNext(e); }
        public IDisposable Subscribe(IObserver<CameraEvent> observer)
        {
            _obs.Add(observer);
            return new Unsub(_obs, observer);
        }
        private sealed class Unsub : IDisposable
        {
            private readonly List<IObserver<CameraEvent>> _l; private readonly IObserver<CameraEvent> _o;
            public Unsub(List<IObserver<CameraEvent>> l, IObserver<CameraEvent> o) { _l = l; _o = o; }
            public void Dispose() => _l.Remove(_o);
        }
    }

    private sealed class Settings : IUserSettingsAccessor
    {
        public string? RecordingsDirectoryOverride => null;
        public int MaxConcurrentGridSessions => 9;
        public string? PreferredNetworkInterface => null;
        public bool SshStrictHostKey => true;
        public int SshDefaultPort => 22;
        public string MajesticConfigPath => "/etc/majestic.yaml";
        public AiAcceleration AiAcceleration => AiAcceleration.Auto;
        public int ActiveLayoutId => 0;
        public bool NotificationsEnabled { get; init; } = true;
        public bool NotifyOnMotion { get; init; } = true;
        public bool NotifyOnDetection { get; init; } = true;
        public int NotificationCooldownSeconds { get; init; } = 30;
        public bool QuietHoursEnabled { get; init; }
        public int QuietHoursStartHour { get; init; } = 22;
        public int QuietHoursEndHour { get; init; } = 7;
        public int NotifyMinConfidencePercent { get; init; }
        public int NotifyMinDurationSeconds { get; init; }
        public int NotifyMinObjects { get; init; } = 1;
        public EventActionSettings EventActions { get; init; } = EventActionSettings.Default;
    }
}
