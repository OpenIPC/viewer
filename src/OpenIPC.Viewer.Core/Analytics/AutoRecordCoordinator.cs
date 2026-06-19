using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.Core.Analytics;

// Drives auto-record from detections (Phase 15.6). Subscribes to the engine's
// results; when a camera with AutoRecord on detects something it starts
// recording and arms a smart-stop window. A 1 Hz tick stops recordings whose
// window has gone quiet. Only stops recordings IT started — a manual recording
// is never auto-stopped. Stop goes through RecordingService for a graceful MP4
// close (the dashboard's corrupted-file trap).
public sealed class AutoRecordCoordinator : IDisposable
{
    private const int CooldownSeconds = 10;

    private readonly IAnalyticsEngine _engine;
    private readonly RecordingService _recording;
    private readonly ICameraRepository _cameras;

    private readonly ConcurrentDictionary<CameraId, AutoRecordWindow> _windows = new();
    private readonly HashSet<CameraId> _autoStarted = new();
    private readonly object _startedGate = new();

    private IDisposable? _subscription;
    private Timer? _tick;

    // Surfaces start/stop failures to the App layer for logging — Core stays
    // package-dep free (same pattern as RecordingService.BookkeepingFailed).
    public event EventHandler<Exception>? Failed;

    public AutoRecordCoordinator(IAnalyticsEngine engine, RecordingService recording,
        ICameraRepository cameras)
    {
        _engine = engine;
        _recording = recording;
        _cameras = cameras;
    }

    public void Start()
    {
        _subscription ??= _engine.Results.Subscribe(new ResultObserver(r => _ = OnResultAsync(r)));
        _tick ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task OnResultAsync(DetectionResult result)
    {
        try
        {
            if (result.Detections.Count == 0) return;

            // Live read so toggling AutoRecord off takes effect immediately.
            var camera = await _cameras.GetAsync(result.CameraId, CancellationToken.None).ConfigureAwait(false);
            var settings = camera?.AnalyticsOrDefault;
            if (settings is null || !settings.AutoRecord) return;

            var window = _windows.GetOrAdd(result.CameraId,
                _ => new AutoRecordWindow(settings.PostEventSeconds, CooldownSeconds));

            if (window.OnDetection(result.OccurredAt) != AutoRecordAction.Start) return;
            if (_recording.IsRecording(result.CameraId)) return; // manual recording already running

            await _recording.StartAsync(result.CameraId, CancellationToken.None).ConfigureAwait(false);
            lock (_startedGate) _autoStarted.Add(result.CameraId);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
    }

    private async Task TickAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _windows)
        {
            if (kv.Value.OnTick(now) != AutoRecordAction.Stop) continue;

            bool ours;
            lock (_startedGate) ours = _autoStarted.Remove(kv.Key);
            if (!ours || !_recording.IsRecording(kv.Key)) continue;

            try
            {
                await _recording.StopAsync(kv.Key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, ex);
            }
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _tick?.Dispose();
    }

    private sealed class ResultObserver : IObserver<DetectionResult>
    {
        private readonly Action<DetectionResult> _onNext;
        public ResultObserver(Action<DetectionResult> onNext) => _onNext = onNext;
        public void OnNext(DetectionResult value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
