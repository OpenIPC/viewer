using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Core.Notifications;

// The notification brain (Phase 19.3): subscribes to the camera-event stream and
// decides what actually surfaces — master switch, per-kind toggles, per-camera
// cooldown (anti-spam, their dashboard's lesson) and optional quiet hours. The
// policy (ShouldNotify) is pure + unit-tested; delivery goes to the native
// INotificationService. Lives in Core (no package deps), takes IObservable so
// tests can feed a Subject instead of the real ingestion service.
public sealed class NotificationCoordinator : IDisposable
{
    private readonly IObservable<CameraEvent> _events;
    private readonly INotificationService _sink;
    private readonly IUserSettingsAccessor _settings;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<CameraId, DateTime> _lastShown = new();

    private IDisposable? _sub;

    public NotificationCoordinator(
        IObservable<CameraEvent> events,
        INotificationService sink,
        IUserSettingsAccessor settings,
        Func<DateTime>? clock = null)
    {
        _events = events;
        _sink = sink;
        _settings = settings;
        _now = clock ?? (() => DateTime.Now); // quiet hours are local-time
    }

    public void Start()
    {
        if (!_sink.IsAvailable) return;
        _sub ??= _events.Subscribe(new Observer(this));
    }

    // Pure decision (per-camera cooldown uses _lastShown). Does NOT mutate state.
    public bool ShouldNotify(CameraEvent e, DateTime nowLocal)
    {
        if (!_settings.NotificationsEnabled) return false;

        var kindEnabled = e.Kind switch
        {
            EventKind.Motion => _settings.NotifyOnMotion,
            EventKind.Detection => _settings.NotifyOnDetection,
            _ => false, // Connection / Snapshot not surfaced in v1
        };
        if (!kindEnabled) return false;

        if (_settings.QuietHoursEnabled &&
            InQuietHours(nowLocal.Hour, _settings.QuietHoursStartHour, _settings.QuietHoursEndHour))
            return false;

        lock (_gate)
        {
            if (_lastShown.TryGetValue(e.CameraId, out var last))
            {
                var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.NotificationCooldownSeconds));
                if (nowLocal - last < cooldown) return false;
            }
        }
        return true;
    }

    // Inclusive-start, exclusive-end hour window; wraps past midnight when
    // start > end (e.g. 22→7). start == end means "no quiet window". Public for tests.
    public static bool InQuietHours(int hour, int start, int end)
    {
        if (start == end) return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private void OnEvent(CameraEvent e)
    {
        var now = _now();
        if (!ShouldNotify(e, now)) return;
        lock (_gate) _lastShown[e.CameraId] = now;

        var (title, body) = Describe(e);
        _sink.Show(new NotificationRequest(title, body, e.Kind, e.CameraId));
    }

    private static (string title, string body) Describe(CameraEvent e)
    {
        var title = e.Kind == EventKind.Detection ? "Object detected" : "Motion detected";
        var body = string.IsNullOrWhiteSpace(e.Summary) ? "Camera event" : e.Summary!;
        return (title, body);
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
    }

    private sealed class Observer : IObserver<CameraEvent>
    {
        private readonly NotificationCoordinator _owner;
        public Observer(NotificationCoordinator owner) => _owner = owner;
        public void OnNext(CameraEvent value) => _owner.OnEvent(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
