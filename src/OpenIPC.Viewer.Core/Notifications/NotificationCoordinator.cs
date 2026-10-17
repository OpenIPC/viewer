using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Core.Notifications;

// The notification brain (Phase 19.3): subscribes to the camera-event stream and
// decides what actually surfaces — master switch, per-kind toggles, thresholds,
// per-camera cooldown (anti-spam, their dashboard's lesson) and optional quiet
// hours. Delivery goes to the native INotificationService and, separately, to
// the external actions (IEventActionDispatcher). Lives in Core (no package
// deps), takes IObservable so tests can feed a Subject instead of the real
// ingestion service.
//
// Each event episode is delivered at most once per channel. The ingestion
// stream publishes an episode several times (open, peak updates, close), so the
// coordinator tracks open episodes by EventId and fires the first time one
// clears every threshold:
//   - confidence / object count (detections): an update that raises the peak
//     re-evaluates the episode;
//   - duration: a timer re-checks the episode once it is old enough, so a long
//     event notifies while it is still going on, not when it ends.
public sealed class NotificationCoordinator : IDisposable
{
    private readonly IObservable<CameraEvent> _events;
    private readonly INotificationService _sink;
    private readonly IUserSettingsAccessor _settings;
    private readonly IEventActionDispatcher? _actions;
    private readonly Func<CameraId, CancellationToken, Task<string?>>? _cameraName;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly object _gate = new();
    private readonly Dictionary<CameraId, DateTime> _lastShown = new();
    private readonly Dictionary<CameraId, DateTime> _lastAction = new();
    private readonly Dictionary<EventId, Episode> _episodes = new();
    private readonly CancellationTokenSource _cts = new();

    private IDisposable? _sub;

    public NotificationCoordinator(
        IObservable<CameraEvent> events,
        INotificationService sink,
        IUserSettingsAccessor settings,
        Func<DateTime>? clock = null,
        IEventActionDispatcher? actions = null,
        Func<CameraId, CancellationToken, Task<string?>>? cameraName = null,
        Func<TimeSpan, Action, IDisposable>? scheduler = null)
    {
        _events = events;
        _sink = sink;
        _settings = settings;
        _actions = actions;
        _cameraName = cameraName;
        _now = clock ?? (() => DateTime.Now); // quiet hours are local-time
        _schedule = scheduler ?? DefaultSchedule;
    }

    public void Start()
    {
        // Nothing to deliver to: no native sink and no action dispatcher.
        if (!_sink.IsAvailable && _actions is null) return;
        _sub ??= _events.Subscribe(new Observer(this));
    }

    // Pure decision for the on-screen channel at the moment of delivery: master
    // switch, kind toggle, quiet hours and the per-camera cooldown. Thresholds are
    // checked separately (PassesThresholds). Does NOT mutate state.
    public bool ShouldNotify(CameraEvent e, DateTime nowLocal)
    {
        lock (_gate) return ShouldNotifyLocked(e, nowLocal);
    }

    // Same idea for the external actions: their own switch and cooldown, and
    // quiet hours only when the user asked actions to honour them.
    public bool ShouldRunActions(CameraEvent e, DateTime nowLocal)
    {
        lock (_gate) return ShouldRunActionsLocked(e, nowLocal);
    }

    public enum ThresholdResult
    {
        Pass,
        // Not yet — a later update (higher peak) may still pass.
        WaitForPeak,
        // Not yet — the episode is still too short.
        WaitForDuration,
        // Can no longer pass (the episode ended below a threshold).
        Fail,
    }

    // Confidence and object count apply to detections only (a plain motion tick
    // carries neither); duration applies to every event.
    public ThresholdResult PassesThresholds(CameraEvent e, DateTime nowUtc)
    {
        var closed = e.EndedAt is not null;

        if (e.Kind == EventKind.Detection)
        {
            var minConfidence = Math.Max(0, Math.Min(100, _settings.NotifyMinConfidencePercent));
            if (minConfidence > 0 && (e.PeakConfidence ?? 0f) * 100f + 0.0001f < minConfidence)
                return closed ? ThresholdResult.Fail : ThresholdResult.WaitForPeak;

            var minObjects = Math.Max(1, _settings.NotifyMinObjects);
            if (minObjects > 1 && e.PeakObjects < minObjects)
                return closed ? ThresholdResult.Fail : ThresholdResult.WaitForPeak;
        }

        var minDuration = Math.Max(0, _settings.NotifyMinDurationSeconds);
        if (minDuration > 0)
        {
            var end = e.EndedAt ?? nowUtc;
            var lasted = end.ToUniversalTime() - e.OccurredAt.ToUniversalTime();
            if (lasted < TimeSpan.FromSeconds(minDuration))
                return closed ? ThresholdResult.Fail : ThresholdResult.WaitForDuration;
        }

        return ThresholdResult.Pass;
    }

    // Inclusive-start, exclusive-end hour window; wraps past midnight when
    // start > end (e.g. 22→7). start == end means "no quiet window". Public for tests.
    public static bool InQuietHours(int hour, int start, int end)
    {
        if (start == end) return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private bool KindEnabled(EventKind kind) => kind switch
    {
        EventKind.Motion => _settings.NotifyOnMotion,
        EventKind.Detection => _settings.NotifyOnDetection,
        _ => false, // Connection / Snapshot not surfaced in v1
    };

    private static bool CooldownElapsed(Dictionary<CameraId, DateTime> last, CameraId camera, DateTime now, int seconds)
    {
        if (!last.TryGetValue(camera, out var at)) return true;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return now - at >= cooldown;
    }

    private void OnEvent(CameraEvent e)
    {
        bool notify = false, act = false;
        lock (_gate)
        {
            if (!_episodes.TryGetValue(e.Id, out var ep))
            {
                ep = new Episode();
                _episodes[e.Id] = ep;
            }
            ep.Latest = e;
            Evaluate(ep, out notify, out act);
            if (e.EndedAt is not null)
            {
                ep.Recheck?.Dispose();
                _episodes.Remove(e.Id);
            }
        }

        if (notify || act)
            _ = DeliverAsync(e, notify, act);
    }

    // Caller holds _gate. Decides which channels fire now and arms the duration
    // re-check when that is the only thing still missing.
    private void Evaluate(Episode ep, out bool notify, out bool act)
    {
        notify = act = false;
        if (ep.NotifyDone && ep.ActionsDone) return;

        var e = ep.Latest!;
        var nowLocal = _now();
        switch (PassesThresholds(e, nowLocal.ToUniversalTime()))
        {
            case ThresholdResult.Pass:
                break;
            case ThresholdResult.WaitForDuration:
                ArmRecheck(ep, e);
                return;
            default:
                return;
        }

        if (!ep.NotifyDone)
        {
            ep.NotifyDone = true;
            if (ShouldNotifyLocked(e, nowLocal))
            {
                _lastShown[e.CameraId] = nowLocal;
                notify = true;
            }
        }
        if (!ep.ActionsDone)
        {
            ep.ActionsDone = true;
            if (ShouldRunActionsLocked(e, nowLocal))
            {
                _lastAction[e.CameraId] = nowLocal;
                act = true;
            }
        }
    }

    // Caller holds _gate (the cooldown maps are guarded by it).
    private bool ShouldNotifyLocked(CameraEvent e, DateTime nowLocal)
    {
        if (!_settings.NotificationsEnabled || !_sink.IsAvailable || !KindEnabled(e.Kind)) return false;
        if (_settings.QuietHoursEnabled &&
            InQuietHours(nowLocal.Hour, _settings.QuietHoursStartHour, _settings.QuietHoursEndHour))
            return false;
        return CooldownElapsed(_lastShown, e.CameraId, nowLocal, _settings.NotificationCooldownSeconds);
    }

    private bool ShouldRunActionsLocked(CameraEvent e, DateTime nowLocal)
    {
        if (_actions is null) return false;
        var a = _settings.EventActions ?? EventActionSettings.Default;
        if (!a.Enabled || !a.AnyChannelEnabled || !KindEnabled(e.Kind)) return false;
        if (!a.IgnoreQuietHours && _settings.QuietHoursEnabled &&
            InQuietHours(nowLocal.Hour, _settings.QuietHoursStartHour, _settings.QuietHoursEndHour))
            return false;
        return CooldownElapsed(_lastAction, e.CameraId, nowLocal, a.CooldownSeconds);
    }

    private void ArmRecheck(Episode ep, CameraEvent e)
    {
        if (ep.Recheck is not null) return;
        var due = e.OccurredAt.ToUniversalTime()
                  + TimeSpan.FromSeconds(Math.Max(0, _settings.NotifyMinDurationSeconds))
                  - _now().ToUniversalTime();
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        var id = e.Id;
        ep.Recheck = _schedule(due + TimeSpan.FromMilliseconds(50), () => OnRecheck(id));
    }

    private void OnRecheck(EventId id)
    {
        bool notify = false, act = false;
        CameraEvent? e;
        lock (_gate)
        {
            if (!_episodes.TryGetValue(id, out var ep)) return;
            ep.Recheck?.Dispose();
            ep.Recheck = null;
            e = ep.Latest;
            Evaluate(ep, out notify, out act);
        }
        if (e is not null && (notify || act))
            _ = DeliverAsync(e, notify, act);
    }

    private async Task DeliverAsync(CameraEvent e, bool notify, bool act)
    {
        try
        {
            string? name = null;
            if (_cameraName is not null)
            {
                try { name = await _cameraName(e.CameraId, _cts.Token).ConfigureAwait(false); }
                catch (Exception) { /* fall back to the id */ }
            }
            var cameraName = string.IsNullOrWhiteSpace(name) ? e.CameraId.ToString() : name!;
            var title = TitleFor(e.Kind);

            if (notify)
            {
                var hasName = !string.IsNullOrWhiteSpace(name);
                _sink.Show(new NotificationRequest(title, Describe(e, hasName ? name : null), e.Kind, e.CameraId));
            }

            if (act && _actions is not null)
            {
                var ctx = new EventActionContext(e, cameraName, title, _now().ToUniversalTime());
                await _actions.DispatchAsync(ctx, _settings.EventActions ?? EventActionSettings.Default, _cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A sink or dispatcher must never take the event stream down.
        }
    }

    public static string TitleFor(EventKind kind) =>
        kind == EventKind.Detection ? "Object detected" : "Motion detected";

    // "Front door · person ×2 (87%)"; the camera prefix is left out when unknown.
    public static string Describe(CameraEvent e, string? cameraName)
    {
        var body = string.IsNullOrWhiteSpace(e.Summary) ? "Camera event" : e.Summary!;
        if (e.Kind == EventKind.Detection && e.PeakConfidence is { } c)
            body += string.Format(CultureInfo.InvariantCulture, " ({0}%)", (int)Math.Round(c * 100));
        return string.IsNullOrWhiteSpace(cameraName) ? body : cameraName + " · " + body;
    }

    private static IDisposable DefaultSchedule(TimeSpan due, Action callback) =>
        new Timer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
        _cts.Cancel();
        lock (_gate)
        {
            foreach (var ep in _episodes.Values) ep.Recheck?.Dispose();
            _episodes.Clear();
        }
        _cts.Dispose();
    }

    private sealed class Episode
    {
        public CameraEvent? Latest { get; set; }
        public bool NotifyDone { get; set; }
        public bool ActionsDone { get; set; }
        public IDisposable? Recheck { get; set; }
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
