using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Video;

// Owns the live-session registry. Tiles and SingleCameraPage all call
// GetOrCreate; only the last Release on a key actually disposes. This
// is what keeps "open grid (substream) -> tap tile -> single (mainstream)
// -> back" from killing the wrong session.
public sealed class LiveStreamCoordinator : IAsyncDisposable
{
    private readonly IVideoEngine _engine;
    private readonly Dictionary<SessionKey, Entry> _entries = new();
    private readonly object _gate = new();

    public int MaxConcurrentSessions { get; set; } = 16;

    public LiveStreamCoordinator(IVideoEngine engine)
    {
        _engine = engine;
    }

    public IVideoSession Acquire(CameraId id, StreamQuality quality, VideoSessionOptions options)
    {
        var key = new SessionKey(id, quality);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return existing.Session;
            }

            if (_entries.Count >= MaxConcurrentSessions)
                throw new TooManySessionsException(MaxConcurrentSessions);

            var session = _engine.CreateSession(options);
            _entries[key] = new Entry { Session = session, RefCount = 1 };
            return session;
        }
    }

    public async Task ReleaseAsync(CameraId id, StreamQuality quality)
    {
        IVideoSession? toDispose = null;
        var key = new SessionKey(id, quality);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                _entries.Remove(key);
                toDispose = entry.Session;
            }
        }

        if (toDispose is not null)
            await toDispose.DisposeAsync().ConfigureAwait(false);
    }

    public async Task ReleaseAllAsync()
    {
        IVideoSession[] sessions;
        lock (_gate)
        {
            sessions = new IVideoSession[_entries.Count];
            var i = 0;
            foreach (var e in _entries.Values)
                sessions[i++] = e.Session;
            _entries.Clear();
        }

        foreach (var s in sessions)
        {
            try { await s.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow during teardown */ }
        }
    }

    public ValueTask DisposeAsync() => new(ReleaseAllAsync());

    private readonly record struct SessionKey(CameraId CameraId, StreamQuality Quality);

    private sealed class Entry
    {
        public IVideoSession Session = default!;
        public int RefCount;
    }
}
