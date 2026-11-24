using System;
using System.Collections.Generic;
using System.Linq;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Status;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>Raised when a camera hits the consecutive-failure threshold.</summary>
public sealed class ConnectionThresholdEventArgs : EventArgs
{
    public ConnectionThresholdEventArgs(CameraId cameraId, int consecutiveFailures)
    {
        CameraId = cameraId;
        ConsecutiveFailures = consecutiveFailures;
    }

    public CameraId CameraId { get; }
    public int ConsecutiveFailures { get; }
}

/// <summary>Raised when a camera streams again after an outage.</summary>
public sealed class ConnectionRecoveredEventArgs : EventArgs
{
    public ConnectionRecoveredEventArgs(CameraId cameraId, TimeSpan outage, int failedAttempts)
    {
        CameraId = cameraId;
        Outage = outage;
        FailedAttempts = failedAttempts;
    }

    public CameraId CameraId { get; }
    public TimeSpan Outage { get; }
    public int FailedAttempts { get; }
}

// Counts connections, drops and failed attempts per camera from the live-session
// state stream every tile already reports into CameraStatusRegistry. The reconnect
// wrapper forwards the inner session's states, so one dead attempt arrives as
// "Connecting → Failed → Reconnecting" and a drop of a live stream as
// "Playing → Failed → Reconnecting": the first failure state after a non-failure
// state is the event, the second is the wrapper's own badge and is ignored.
//
// One camera can be on screen in several tiles at once (grid + single view,
// main + sub stream), each with its own session. States are therefore kept per
// reporting tile, and the camera is "live" while ANY tile streams: a connection
// is the camera going from no live tile to one, a drop is the last live tile
// failing, and a failed attempt only counts while no tile streams — one tile
// closing or retrying while another plays is not a network event.
//
// Thread-safe. Events fire outside the lock.
public sealed class ConnectionJournal : IDisposable
{
    /// <summary>Consecutive failed attempts that trigger an automatic check.</summary>
    public const int FailureThreshold = 3;

    /// <summary>Upper bound of the event list (oldest dropped first).</summary>
    public const int MaxEvents = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<CameraId, CameraState> _cameras = new();
    private readonly List<ConnectionEvent> _events = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly CameraStatusRegistry? _registry;

    public ConnectionJournal(CameraStatusRegistry? registry = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _registry = registry;
        if (_registry is not null)
            _registry.SessionReported += OnSessionReported;
    }

    /// <summary>A camera reached <see cref="FailureThreshold"/> failed attempts in a row (once per outage).</summary>
    public event EventHandler<ConnectionThresholdEventArgs>? ThresholdReached;

    /// <summary>A camera that was down is streaming again.</summary>
    public event EventHandler<ConnectionRecoveredEventArgs>? Recovered;

    /// <summary>Counters or events changed (for persistence and UI refresh).</summary>
    public event EventHandler? Changed;

    public ConnectionCounters Get(CameraId id)
    {
        lock (_gate)
            return _cameras.TryGetValue(id, out var s) ? s.Counters : ConnectionCounters.Empty;
    }

    /// <summary>True while the camera is between a drop/failed attempt and the next frame.</summary>
    public bool IsInOutage(CameraId id)
    {
        lock (_gate)
            return _cameras.TryGetValue(id, out var s) && s.OutageStart is not null;
    }

    public IReadOnlyList<ConnectionEvent> Events(CameraId? id = null)
    {
        lock (_gate)
            return id is { } cam
                ? _events.Where(e => e.CameraId == cam).ToList()
                : _events.ToList();
    }

    /// <summary>Adds a free-form entry (auto check / auto fix) to the journal.</summary>
    public void Record(CameraId id, ConnectionEventKind kind, string? detail = null)
    {
        lock (_gate)
            AddEvent(new ConnectionEvent(id, _clock(), kind, detail));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Key for callers that don't say which tile reports (one tile per camera).
    private static readonly object DefaultSource = new();

    /// <summary>
    /// Feeds one live-session state from one reporting tile (<paramref name="source"/>,
    /// any stable per-tile object); null = that tile went away.
    /// </summary>
    public void Report(CameraId id, SessionState? state, object? source = null)
    {
        ConnectionThresholdEventArgs? threshold = null;
        ConnectionRecoveredEventArgs? recovered = null;
        var changed = false;

        lock (_gate)
        {
            if (!_cameras.TryGetValue(id, out var s))
            {
                s = new CameraState();
                _cameras[id] = s;
            }

            var key = source ?? DefaultSource;
            SessionState? prev = s.Tiles.TryGetValue(key, out var p) ? p : null;
            if (prev == state)
                return;
            if (state is { } st) s.Tiles[key] = st;
            else s.Tiles.Remove(key);

            var wasLive = s.Live;
            var nowLive = s.Tiles.Values.Any(t => IsLive(t));
            s.Live = nowLive;
            var now = _clock();

            if (!wasLive && nowLive)
            {
                // The camera streams again (first tile live, or back after an outage).
                var c = s.Counters;
                if (s.OutageStart is { } start)
                {
                    var outage = now - start;
                    recovered = new ConnectionRecoveredEventArgs(id, outage, c.ConsecutiveFailures);
                    c = c with { LastRecoveredAt = now, LastOutage = outage };
                    AddEvent(new ConnectionEvent(id, now, ConnectionEventKind.Recovered,
                        ((long)outage.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                else
                {
                    AddEvent(new ConnectionEvent(id, now, ConnectionEventKind.Connected));
                }
                s.Counters = c with
                {
                    Connects = c.Connects + 1,
                    ConsecutiveFailures = 0,
                    LastConnectedAt = now,
                };
                s.OutageStart = null;
                s.ThresholdRaised = false;
                changed = true;
            }
            else if (wasLive && !nowLive)
            {
                // The last live tile stopped. Only its failure is a drop; closing
                // it or the user's retry / quality swap is not a network event.
                if (IsFailure(state))
                {
                    s.Counters = s.Counters with { Drops = s.Counters.Drops + 1, LastDropAt = now };
                    s.OutageStart = now;
                    AddEvent(new ConnectionEvent(id, now, ConnectionEventKind.Dropped));
                    changed = true;
                }
            }
            else if (!nowLive && IsFailure(state) && !IsFailure(prev))
            {
                // An attempt of this tile failed and no tile streams the camera.
                // (The first failure state after a non-failure one; the wrapper's
                // own Reconnecting badge right after it is not a second attempt.)
                var consecutive = s.Counters.ConsecutiveFailures + 1;
                s.Counters = s.Counters with
                {
                    FailedAttempts = s.Counters.FailedAttempts + 1,
                    ConsecutiveFailures = consecutive,
                };
                s.OutageStart ??= now;
                AddEvent(new ConnectionEvent(id, now, ConnectionEventKind.FailedAttempt,
                    consecutive.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (consecutive >= FailureThreshold && !s.ThresholdRaised)
                {
                    s.ThresholdRaised = true;
                    threshold = new ConnectionThresholdEventArgs(id, consecutive);
                }
                changed = true;
            }
            // wasLive && nowLive: another tile still streams — whatever this tile
            // does (fails, retries, closes) the camera itself is reachable.
        }

        if (threshold is not null) ThresholdReached?.Invoke(this, threshold);
        if (recovered is not null) Recovered?.Invoke(this, recovered);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears counters and events for one camera, or for all when null.</summary>
    public void Reset(CameraId? id = null)
    {
        lock (_gate)
        {
            if (id is { } cam)
            {
                if (_cameras.TryGetValue(cam, out var s))
                {
                    s.Counters = ConnectionCounters.Empty;
                    s.OutageStart = null;
                    s.ThresholdRaised = false;
                }
                _events.RemoveAll(e => e.CameraId == cam);
            }
            else
            {
                foreach (var s in _cameras.Values)
                {
                    s.Counters = ConnectionCounters.Empty;
                    s.OutageStart = null;
                    s.ThresholdRaised = false;
                }
                _events.Clear();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Counters + events for persistence.</summary>
    public (IReadOnlyDictionary<CameraId, ConnectionCounters> Counters, IReadOnlyList<ConnectionEvent> Events) Export()
    {
        lock (_gate)
            return (_cameras.ToDictionary(kv => kv.Key, kv => kv.Value.Counters), _events.ToList());
    }

    /// <summary>
    /// Restores persisted counters/events. Live state is not restored: an outage
    /// that was open when the app closed is not carried over, and the consecutive
    /// failure count starts again from zero (a fresh run, fresh attempts).
    /// </summary>
    public void Import(IReadOnlyDictionary<CameraId, ConnectionCounters> counters, IEnumerable<ConnectionEvent> events)
    {
        lock (_gate)
        {
            foreach (var kv in counters)
            {
                if (!_cameras.TryGetValue(kv.Key, out var s))
                {
                    s = new CameraState();
                    _cameras[kv.Key] = s;
                }
                s.Counters = kv.Value with { ConsecutiveFailures = 0 };
            }
            _events.Clear();
            _events.AddRange(events.OrderBy(e => e.At));
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    public void Dispose()
    {
        if (_registry is not null)
            _registry.SessionReported -= OnSessionReported;
    }

    private void OnSessionReported(object? sender, CameraSessionReport e) => Report(e.CameraId, e.State, e.Source);

    private void AddEvent(ConnectionEvent e)
    {
        _events.Add(e);
        if (_events.Count > MaxEvents)
            _events.RemoveRange(0, _events.Count - MaxEvents);
    }

    private static bool IsLive(SessionState? s) => s is SessionState.Playing or SessionState.Paused;
    private static bool IsFailure(SessionState? s) => s is SessionState.Failed or SessionState.Reconnecting;

    private sealed class CameraState
    {
        // Per reporting tile; a tile that went away (null) is removed.
        public readonly Dictionary<object, SessionState> Tiles = new();
        // Any tile Playing/Paused.
        public bool Live;
        public ConnectionCounters Counters = ConnectionCounters.Empty;
        public DateTimeOffset? OutageStart;
        public bool ThresholdRaised;
    }
}
