using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Core.Recording;

// Tracks the (CameraId → IRecordingSession) map and pushes segment metadata
// into IRecordingRepository as ffmpeg rotates files. UI subscribes to
// StateChanged to repaint REC indicators.
public sealed class RecordingService : IAsyncDisposable
{
    private readonly IRecorder _recorder;
    private readonly IRecordingRepository _repo;
    private readonly IFileSystem _fs;
    private readonly CameraDirectoryService _cameras;
    private readonly IUserSettingsAccessor? _userSettings;

    private readonly object _gate = new();
    private readonly Dictionary<CameraId, Handle> _active = new();
    private readonly Dictionary<CameraId, string> _lastError = new();

    public event EventHandler<CameraId>? StateChanged;

    // Surfaces repo-side errors during segment bookkeeping. UI/App layer
    // attaches logging here — Core stays package-dep free.
    public event EventHandler<Exception>? BookkeepingFailed;

    // A session that ended without the user stopping it (camera unreachable,
    // stream dropped, disk refused the file). The message is the recorder's own.
    public event EventHandler<RecordingFailure>? RecordingFailed;

    public RecordingService(
        IRecorder recorder,
        IRecordingRepository repo,
        IFileSystem fs,
        CameraDirectoryService cameras,
        IUserSettingsAccessor? userSettings = null)
    {
        _recorder = recorder;
        _repo = repo;
        _fs = fs;
        _cameras = cameras;
        _userSettings = userSettings;
    }

    public bool IsRecording(CameraId id)
    {
        lock (_gate) return _active.ContainsKey(id);
    }

    public DateTime? StartedAt(CameraId id)
    {
        lock (_gate) return _active.TryGetValue(id, out var h) ? h.Session.StartedAt : null;
    }

    public async Task ToggleAsync(CameraId id, CancellationToken ct)
    {
        if (IsRecording(id))
            await StopAsync(id, ct).ConfigureAwait(false);
        else
            await StartAsync(id, ct).ConfigureAwait(false);
    }

    public async Task StartAsync(CameraId id, CancellationToken ct)
    {
        if (IsRecording(id)) return;

        var camera = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");
        var creds = await _cameras.GetCredentialsAsync(id, ct).ConfigureAwait(false);

        // User-picked override (Settings → Recording) takes precedence over
        // the platform IFileSystem.RecordingsDir default. Empty/missing
        // means "use the default".
        var root = !string.IsNullOrWhiteSpace(_userSettings?.RecordingsDirectoryOverride)
            ? _userSettings!.RecordingsDirectoryOverride!
            : _fs.RecordingsDir.FullName;
        var dir = Path.Combine(
            root,
            Slug(camera.Name),
            DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        var options = new RecordingOptions(
            CameraId: id,
            RtspUri: camera.RtspMainUri,
            Credentials: creds,
            OutputDirectory: dir,
            FilenamePattern: "cam_%Y%m%d_%H%M%S.mp4",
            SegmentDuration: TimeSpan.FromMinutes(10));

        var session = await _recorder.StartAsync(options, ct).ConfigureAwait(false);
        // Adapter exists because Core has no System.Reactive (no package deps);
        // BCL only exposes IObservable.Subscribe(IObserver<T>).
        var sub = session.Events.Subscribe(new CallbackObserver(ev => _ = HandleEventAsync(id, session, ev)));
        lock (_gate) _active[id] = new Handle(session, sub);
        StateChanged?.Invoke(this, id);
    }

    public async Task StopAsync(CameraId id, CancellationToken ct)
    {
        Handle? h;
        lock (_gate)
        {
            _active.TryGetValue(id, out h);
            if (h is not null) _active.Remove(id);
        }
        if (h is null) return;

        try { await h.Session.StopAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { BookkeepingFailed?.Invoke(this, ex); }

        // Finalize last segment row with EndedAt + final size.
        if (h.Session.CurrentSegmentPath is { } lastPath)
            await FinalizeSegmentAsync(lastPath, DateTime.UtcNow).ConfigureAwait(false);

        h.Subscription.Dispose();
        try { await h.Session.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        StateChanged?.Invoke(this, id);
    }

    private async Task HandleEventAsync(CameraId id, IRecordingSession session, RecordingEvent ev)
    {
        try
        {
            switch (ev)
            {
                case RecordingEvent.Started s:
                    await _repo.AddAsync(NewRow(id, s.FirstSegmentPath, s.Time), CancellationToken.None).ConfigureAwait(false);
                    break;

                case RecordingEvent.SegmentRotated r:
                    await FinalizeSegmentAsync(r.PrevPath, r.Time, r.Size).ConfigureAwait(false);
                    await _repo.AddAsync(NewRow(id, r.NewPath, r.Time), CancellationToken.None).ConfigureAwait(false);
                    break;

                case RecordingEvent.Error err:
                    lock (_gate) _lastError[id] = err.Message;
                    BookkeepingFailed?.Invoke(this, new InvalidOperationException($"Recorder error: {err.Message}"));
                    break;

                case RecordingEvent.Stopped st when st.Reason != RecordingStopReason.User:
                    await OnSessionEndedAsync(id, session, st).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            BookkeepingFailed?.Invoke(this, ex);
        }
    }

    // The recorder gave up on its own. Without this the camera stayed "recording"
    // with a ticking REC timer and no file behind it, the last row never got
    // EndedAt, and a fresh Start was refused as "already recording".
    private async Task OnSessionEndedAsync(CameraId id, IRecordingSession session, RecordingEvent.Stopped st)
    {
        // Leave the recorder's OnNext before disposing the session completes
        // its event stream.
        await Task.Yield();

        Handle? h;
        string? message;
        lock (_gate)
        {
            // StopAsync removes the handle before stopping, so a missing or different
            // handle means this session is already being torn down elsewhere.
            if (!_active.TryGetValue(id, out h) || !ReferenceEquals(h.Session, session)) return;
            _active.Remove(id);
            _lastError.TryGetValue(id, out message);
            _lastError.Remove(id);
        }

        var lastPath = session.CurrentSegmentPath;
        if (lastPath is not null)
            await FinalizeSegmentAsync(lastPath, st.Time).ConfigureAwait(false);

        h.Subscription.Dispose();
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        StateChanged?.Invoke(this, id);
        RecordingFailed?.Invoke(this, new RecordingFailure(
            id, message ?? st.Detail ?? st.Reason.ToString(), WroteSegment: lastPath is not null));
    }

    private async Task FinalizeSegmentAsync(string path, DateTime endedAt, long? size = null)
    {
        try
        {
            var prev = await _repo.GetByPathAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (prev is null) return;
            var finalSize = size ?? (File.Exists(path) ? new FileInfo(path).Length : prev.SizeBytes);
            await _repo.UpdateAsync(
                prev with { EndedAt = endedAt, SizeBytes = finalSize },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BookkeepingFailed?.Invoke(this, ex);
        }
    }

    private static Recording NewRow(CameraId cam, string path, DateTime started) =>
        new(RecordingId.New(), cam, path, started, EndedAt: null, SizeBytes: 0, Codec: null, HasMotion: false);

    private static string Slug(string name)
    {
        var chars = new List<char>(name.Length);
        foreach (var raw in name.ToLowerInvariant())
        {
            var c = raw;
            if (c == ' ') c = '-';
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_') chars.Add(c);
        }
        var slug = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrEmpty(slug) ? "camera" : slug;
    }

    public async ValueTask DisposeAsync()
    {
        Handle[] handles;
        lock (_gate)
        {
            handles = new Handle[_active.Count];
            var i = 0;
            foreach (var h in _active.Values) handles[i++] = h;
            _active.Clear();
        }
        foreach (var h in handles)
        {
            try { await h.Session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            h.Subscription.Dispose();
            try { await h.Session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    // WroteSegment is false when the recorder never got as far as opening a
    // file — the camera refused or never answered, so nothing reached the
    // Recordings tab.
    public sealed record RecordingFailure(CameraId CameraId, string Message, bool WroteSegment = true);

    private sealed record Handle(IRecordingSession Session, IDisposable Subscription);

    private sealed class CallbackObserver : IObserver<RecordingEvent>
    {
        private readonly Action<RecordingEvent> _onNext;
        public CallbackObserver(Action<RecordingEvent> onNext) => _onNext = onNext;
        public void OnNext(RecordingEvent value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
