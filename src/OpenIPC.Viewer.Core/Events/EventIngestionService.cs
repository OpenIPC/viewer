using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.Core.Events;

// Orchestrates motion-event lifecycle:
//   tick -> debounce (Phase 7 §7.3 "1s after first tick, ignore repeats")
//        -> open CameraEvent in repo with OccurredAt
//        -> 5s of quiet -> close event (set EndedAt, persist)
// Plays back to a single Observed event channel that UI subscribes to once.
public sealed class EventIngestionService : IAsyncDisposable
{
    private readonly IEnumerable<IMotionEventSource> _sources;
    private readonly IEventRepository _repo;
    private readonly CameraDirectoryService _cameras;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CloseAfterQuiet = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<CameraId, OpenEventState> _open = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly List<IObserver<CameraEvent>> _observers = new();
    private CancellationTokenSource? _cts;
    private bool _started;

    public IObservable<CameraEvent> Events => new EventsObservable(this);

    public event EventHandler<CameraEvent>? EventFinalized;

    public EventIngestionService(
        IEnumerable<IMotionEventSource> sources,
        IEventRepository repo,
        CameraDirectoryService cameras)
    {
        _sources = sources;
        _repo = repo;
        _cameras = cameras;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        _started = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var cameras = await _cameras.ListAsync(ct).ConfigureAwait(false);
        foreach (var camera in cameras)
        {
            foreach (var source in _sources)
            {
                var observer = new TickObserver(this);
                var sub = source.Watch(camera.Id, observer, _cts.Token);
                lock (_gate) _subscriptions.Add(sub);
            }
        }
    }

    private void OnTick(MotionTick tick)
    {
        OpenEventState? state;
        bool isNew = false;
        lock (_gate)
        {
            if (!_open.TryGetValue(tick.CameraId, out state))
            {
                state = new OpenEventState(EventId.New(), tick.CameraId, tick.At, tick.Source, tick.Kind);
                _open[tick.CameraId] = state;
                isNew = true;
            }
            state.LastTickAt = tick.At;
            // Latest tick's summary wins (e.g. peak class counts during the episode).
            if (tick.Summary is not null) state.Summary = tick.Summary;
            state.QuietTimer?.Change(CloseAfterQuiet, Timeout.InfiniteTimeSpan);
            state.QuietTimer ??= new Timer(CloseQuietState, state, CloseAfterQuiet, Timeout.InfiniteTimeSpan);
        }

        if (isNew)
            _ = OpenAsync(state);
    }

    private async Task OpenAsync(OpenEventState state)
    {
        var ev = new CameraEvent(
            Id: state.EventId,
            CameraId: state.CameraId,
            Kind: state.Kind,
            Severity: EventSeverity.Info,
            OccurredAt: state.StartedAt,
            EndedAt: null,
            Source: state.Source,
            Summary: state.Summary);
        try { await _repo.AddAsync(ev, CancellationToken.None).ConfigureAwait(false); }
        catch { /* surface via finalize event instead — swallow here */ }

        NotifyObservers(ev);
    }

    private void CloseQuietState(object? boxed)
    {
        if (boxed is not OpenEventState state) return;
        OpenEventState? toClose;
        lock (_gate)
        {
            if (!_open.TryGetValue(state.CameraId, out var current) || current != state)
                return;
            // Race: another tick may have arrived during the timer fire — drop if so.
            if ((DateTime.UtcNow - state.LastTickAt) < CloseAfterQuiet - DebounceWindow)
                return;
            _open.Remove(state.CameraId);
            toClose = state;
            state.QuietTimer?.Dispose();
        }

        _ = FinalizeAsync(toClose);
    }

    private async Task FinalizeAsync(OpenEventState state)
    {
        var finalized = new CameraEvent(
            Id: state.EventId,
            CameraId: state.CameraId,
            Kind: state.Kind,
            Severity: EventSeverity.Info,
            OccurredAt: state.StartedAt,
            EndedAt: state.LastTickAt,
            Source: state.Source,
            Summary: state.Summary);
        try { await _repo.UpdateAsync(finalized, CancellationToken.None).ConfigureAwait(false); }
        catch { /* swallow */ }

        EventFinalized?.Invoke(this, finalized);
        NotifyObservers(finalized);
    }

    private void NotifyObservers(CameraEvent ev)
    {
        IObserver<CameraEvent>[] snapshot;
        lock (_gate) snapshot = _observers.ToArray();
        foreach (var o in snapshot)
        {
            try { o.OnNext(ev); } catch { /* swallow */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        IDisposable[] subs;
        lock (_gate)
        {
            subs = _subscriptions.ToArray();
            _subscriptions.Clear();
            foreach (var st in _open.Values)
                st.QuietTimer?.Dispose();
            _open.Clear();
        }
        foreach (var s in subs) { try { s.Dispose(); } catch { } }
        _cts?.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class OpenEventState
    {
        public EventId EventId { get; }
        public CameraId CameraId { get; }
        public DateTime StartedAt { get; }
        public DateTime LastTickAt { get; set; }
        public string? Source { get; }
        public EventKind Kind { get; }
        public string? Summary { get; set; }
        public Timer? QuietTimer { get; set; }

        public OpenEventState(EventId id, CameraId cam, DateTime started, string? source, EventKind kind)
        {
            EventId = id;
            CameraId = cam;
            StartedAt = started;
            LastTickAt = started;
            Source = source;
            Kind = kind;
        }
    }

    private sealed class TickObserver : IObserver<MotionTick>
    {
        private readonly EventIngestionService _owner;
        public TickObserver(EventIngestionService owner) => _owner = owner;
        public void OnNext(MotionTick value) => _owner.OnTick(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class EventsObservable : IObservable<CameraEvent>
    {
        private readonly EventIngestionService _owner;
        public EventsObservable(EventIngestionService owner) => _owner = owner;
        public IDisposable Subscribe(IObserver<CameraEvent> observer)
        {
            lock (_owner._gate) _owner._observers.Add(observer);
            return new Unsubscribe(_owner, observer);
        }

        private sealed class Unsubscribe : IDisposable
        {
            private readonly EventIngestionService _owner;
            private readonly IObserver<CameraEvent> _observer;
            public Unsubscribe(EventIngestionService owner, IObserver<CameraEvent> obs)
            { _owner = owner; _observer = obs; }
            public void Dispose()
            {
                lock (_owner._gate) _owner._observers.Remove(_observer);
            }
        }
    }
}
