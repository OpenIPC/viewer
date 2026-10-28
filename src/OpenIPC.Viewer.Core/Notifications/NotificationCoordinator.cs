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
// the external actions (IEventActionDispatcher), each with its own switches,
// event types and cooldown. Lives in Core (no package deps), takes IObservable
// so tests can feed a Subject instead of the real ingestion service.
//
// Each event episode is delivered at most once per channel. The ingestion
// stream publishes an episode several times (open, peak rises, once-a-second
// progress, close), so the coordinator tracks open episodes by EventId and
// fires a channel the first time the episode clears every threshold AND that
// channel's gate (cooldown, quiet hours) lets it through — a channel held back
// by its gate gets another chance on the episode's next publication. Duration
// is measured to the latest observed activity (LastSeenAt / EndedAt), never to
// "now": an episode waiting out its quiet window after a single blip has not
// lasted N seconds.
public sealed class NotificationCoordinator : IDisposable
{
    // Closed episodes remembered so a publication arriving late (after the
    // close) can't open a fresh, never-closed episode and fire again.
    private const int ClosedMemory = 256;

    private readonly IObservable<CameraEvent> _events;
    private readonly INotificationService _sink;
    private readonly IUserSettingsAccessor _settings;
    private readonly IEventActionDispatcher? _actions;
    private readonly Func<CameraId, CancellationToken, Task<string?>>? _cameraName;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    // Last delivery per camera, in UTC (local wall-clock time jumps at DST).
    private readonly Dictionary<CameraId, DateTime> _lastShown = new();
    private readonly Dictionary<CameraId, DateTime> _lastAction = new();
    private readonly Dictionary<EventId, Episode> _episodes = new();
    private readonly HashSet<EventId> _closed = new();
    private readonly Queue<EventId> _closedOrder = new();
    private readonly CancellationTokenSource _cts = new();

    private IDisposable? _sub;

    // A sink or dispatcher failure (logged by the host — Core has no logger).
    public event EventHandler<Exception>? DeliveryFailed;

    public NotificationCoordinator(
        IObservable<CameraEvent> events,
        INotificationService sink,
        IUserSettingsAccessor settings,
        Func<DateTime>? clock = null,
        IEventActionDispatcher? actions = null,
        Func<CameraId, CancellationToken, Task<string?>>? cameraName = null)
    {
        _events = events;
        _sink = sink;
        _settings = settings;
        _actions = actions;
        _cameraName = cameraName;
        _now = clock ?? (() => DateTime.Now); // quiet hours are local-time
    }

    public void Start()
    {
        // Nothing to deliver to: no native sink and no action dispatcher.
        if (!_sink.IsAvailable && _actions is null) return;
        _sub ??= _events.Subscribe(new Observer(this));
    }

    // Decision for the on-screen channel at the moment of delivery: master
    // switch, kind toggle, quiet hours and the per-camera cooldown. Thresholds are
    // checked separately (PassesThresholds). Does NOT mutate state.
    public bool ShouldNotify(CameraEvent e, DateTime nowLocal)
    {
        lock (_gate) return ShouldNotifyLocked(e, nowLocal);
    }

    // Same idea for the external actions: their own switch, event types and
    // cooldown, and quiet hours only when the user asked actions to honour them.
    public bool ShouldRunActions(CameraEvent e, DateTime nowLocal)
    {
        lock (_gate) return ShouldRunActionsLocked(e, nowLocal);
    }

    public enum ThresholdResult
    {
        Pass,
        // Not yet — a later update of the open episode may still pass.
        Wait,
        // Can no longer pass (the episode ended below a threshold).
        Fail,
    }

    // Confidence and object count apply to detections only (a plain motion tick
    // carries neither); duration applies to every event.
    public ThresholdResult PassesThresholds(CameraEvent e)
    {
        var miss = e.EndedAt is not null ? ThresholdResult.Fail : ThresholdResult.Wait;

        if (e.Kind == EventKind.Detection)
        {
            var minConfidence = Math.Max(0, Math.Min(100, _settings.NotifyMinConfidencePercent));
            if (minConfidence > 0 && (e.PeakConfidence ?? 0f) * 100f + 0.0001f < minConfidence)
                return miss;

            var minObjects = Math.Max(1, _settings.NotifyMinObjects);
            if (minObjects > 1 && e.PeakObjects < minObjects)
                return miss;
        }

        var minDuration = Math.Max(0, _settings.NotifyMinDurationSeconds);
        if (minDuration > 0)
        {
            var end = e.EndedAt ?? e.LastSeenAt ?? e.OccurredAt;
            var lasted = end.ToUniversalTime() - e.OccurredAt.ToUniversalTime();
            if (lasted < TimeSpan.FromSeconds(minDuration))
                return miss;
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

    private bool InQuietHoursNow(DateTime nowLocal) =>
        _settings.QuietHoursEnabled &&
        InQuietHours(nowLocal.Hour, _settings.QuietHoursStartHour, _settings.QuietHoursEndHour);

    private static bool CooldownElapsed(Dictionary<CameraId, DateTime> last, CameraId camera, DateTime nowUtc, int seconds)
    {
        if (!last.TryGetValue(camera, out var at)) return true;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return nowUtc - at >= cooldown;
    }

    private void OnEvent(CameraEvent e)
    {
        bool notify = false, act = false;
        lock (_gate)
        {
            if (_closed.Contains(e.Id)) return;
            if (!_episodes.TryGetValue(e.Id, out var ep))
            {
                ep = new Episode();
                _episodes[e.Id] = ep;
            }
            Evaluate(ep, e, out notify, out act);
            if (e.EndedAt is not null)
            {
                _episodes.Remove(e.Id);
                RememberClosed(e.Id);
            }
        }

        if (notify || act)
            _ = DeliverAsync(e, notify, act);
    }

    private void RememberClosed(EventId id)
    {
        if (!_closed.Add(id)) return;
        _closedOrder.Enqueue(id);
        if (_closedOrder.Count > ClosedMemory)
            _closed.Remove(_closedOrder.Dequeue());
    }

    // Caller holds _gate. A channel is marked done only when it fires now; one
    // held back by its cooldown or quiet hours stays eligible for the episode's
    // next publication (ongoing activity is re-published about once a second).
    private void Evaluate(Episode ep, CameraEvent e, out bool notify, out bool act)
    {
        notify = act = false;
        if (ep.NotifyDone && ep.ActionsDone) return;
        if (PassesThresholds(e) != ThresholdResult.Pass) return;

        var nowLocal = _now();
        var nowUtc = nowLocal.ToUniversalTime();
        if (!ep.NotifyDone && ShouldNotifyLocked(e, nowLocal))
        {
            ep.NotifyDone = true;
            _lastShown[e.CameraId] = nowUtc;
            notify = true;
        }
        if (!ep.ActionsDone && ShouldRunActionsLocked(e, nowLocal))
        {
            ep.ActionsDone = true;
            _lastAction[e.CameraId] = nowUtc;
            act = true;
        }
    }

    // Caller holds _gate (the cooldown maps are guarded by it).
    private bool ShouldNotifyLocked(CameraEvent e, DateTime nowLocal)
    {
        if (!_settings.NotificationsEnabled || !_sink.IsAvailable) return false;
        var kindOn = e.Kind switch
        {
            EventKind.Motion => _settings.NotifyOnMotion,
            EventKind.Detection => _settings.NotifyOnDetection,
            _ => false, // Connection / Snapshot not surfaced in v1
        };
        if (!kindOn || InQuietHoursNow(nowLocal)) return false;
        return CooldownElapsed(_lastShown, e.CameraId, nowLocal.ToUniversalTime(), _settings.NotificationCooldownSeconds);
    }

    private bool ShouldRunActionsLocked(CameraEvent e, DateTime nowLocal)
    {
        if (_actions is null) return false;
        var a = _settings.EventActions ?? EventActionSettings.Default;
        if (!a.Enabled || !a.AnyChannelEnabled) return false;
        var kindOn = e.Kind switch
        {
            EventKind.Motion => a.OnMotion,
            EventKind.Detection => a.OnDetection,
            _ => false,
        };
        if (!kindOn || (!a.IgnoreQuietHours && InQuietHoursNow(nowLocal))) return false;
        return CooldownElapsed(_lastAction, e.CameraId, nowLocal.ToUniversalTime(), a.CooldownSeconds);
    }

    // The toast and the actions are delivered independently: a throwing sink
    // must not cost the alarm its webhook / e-mail, and vice versa.
    private async Task DeliverAsync(CameraEvent e, bool notify, bool act)
    {
        string? name = null;
        if (_cameraName is not null)
        {
            try { name = await _cameraName(e.CameraId, _cts.Token).ConfigureAwait(false); }
            catch (Exception) { /* fall back to the id */ }
        }
        var hasName = !string.IsNullOrWhiteSpace(name);
        var title = TitleFor(e.Kind);

        if (notify)
        {
            try
            {
                _sink.Show(new NotificationRequest(title, Describe(e, hasName ? name : null), e.Kind, e.CameraId));
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
            }
        }

        if (act && _actions is not null)
        {
            try
            {
                var ctx = new EventActionContext(e, hasName ? name! : e.CameraId.ToString(), title, _now().ToUniversalTime());
                await _actions.DispatchAsync(ctx, _settings.EventActions ?? EventActionSettings.Default, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
            }
        }
    }

    private void RaiseFailed(Exception ex)
    {
        try { DeliveryFailed?.Invoke(this, ex); }
        catch (Exception) { /* a logging handler must not throw back here */ }
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

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
        _cts.Cancel();
        lock (_gate) _episodes.Clear();
        _cts.Dispose();
    }

    private sealed class Episode
    {
        public bool NotifyDone { get; set; }
        public bool ActionsDone { get; set; }
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
