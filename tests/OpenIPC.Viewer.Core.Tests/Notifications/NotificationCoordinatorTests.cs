using System;
using System.Collections.Generic;
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
        var (src, sink, coord) = Make(new Settings());
        src.Emit(Event(EventKind.Motion));
        Assert.Single(sink.Shown);
    }

    [Fact]
    public void Suppresses_WhenMasterOff()
    {
        var (src, sink, _) = Make(new Settings { NotificationsEnabled = false });
        src.Emit(Event(EventKind.Motion));
        Assert.Empty(sink.Shown);
    }

    [Fact]
    public void Suppresses_DetectionWhenTypeOff()
    {
        var (src, sink, _) = Make(new Settings { NotifyOnDetection = false });
        src.Emit(Event(EventKind.Detection));
        Assert.Empty(sink.Shown);
    }

    [Fact]
    public void Cooldown_SuppressesSecondWithinWindow()
    {
        var now = Noon;
        var (src, sink, _) = Make(new Settings { NotificationCooldownSeconds = 30 }, () => now);
        var cam = new CameraId(Guid.NewGuid());

        src.Emit(Event(EventKind.Motion, cam));
        now = now.AddSeconds(10);
        src.Emit(Event(EventKind.Motion, cam));   // within cooldown → dropped
        now = now.AddSeconds(40);
        src.Emit(Event(EventKind.Motion, cam));   // cooldown elapsed → shown

        Assert.Equal(2, sink.Shown.Count);
    }

    [Fact]
    public void Cooldown_IsPerCamera()
    {
        var (src, sink, _) = Make(new Settings { NotificationCooldownSeconds = 30 }, () => Noon);
        src.Emit(Event(EventKind.Motion, new CameraId(Guid.NewGuid())));
        src.Emit(Event(EventKind.Motion, new CameraId(Guid.NewGuid())));
        Assert.Equal(2, sink.Shown.Count); // different cameras, both fire
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

    private static (Subject src, FakeSink sink, NotificationCoordinator coord) Make(Settings s, Func<DateTime>? clock = null)
    {
        var src = new Subject();
        var sink = new FakeSink();
        var coord = new NotificationCoordinator(src, sink, s, clock ?? (() => Noon));
        coord.Start();
        return (src, sink, coord);
    }

    private static CameraEvent Event(EventKind kind, CameraId? cam = null) => new(
        new EventId(Guid.NewGuid()), cam ?? new CameraId(Guid.NewGuid()), kind,
        EventSeverity.Info, DateTime.UtcNow, null, "test", "summary");

    private sealed class FakeSink : INotificationService
    {
        public bool Available = true;
        public bool IsAvailable => Available;
        public List<NotificationRequest> Shown { get; } = new();
        public void Show(NotificationRequest request) => Shown.Add(request);
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
    }
}
