using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;
using ReactiveSubject = System.Reactive.Subjects.Subject<OpenIPC.Viewer.Core.Analytics.DetectionResult>;

namespace OpenIPC.Viewer.Analytics;

// The Phase 15.3 sampling pipeline. Per camera: a FrameSampler thins the
// decoder's ~25 FPS down to analyticsFps, each kept frame is downscaled +
// copied (the decoder owns its buffer) and pushed into a small bounded channel
// with drop-oldest semantics. A single worker drains the channel and runs the
// shared detector, so memory/CPU stay bounded regardless of camera count.
public sealed class ObjectDetectionEngine : IAnalyticsEngine
{
    private const int QueueCapacity = 8;
    private const int DownscaleMaxSide = 640;

    private readonly IObjectDetector _detector;
    private readonly IModelProvider _modelProvider;
    private readonly ILogger<ObjectDetectionEngine> _log;

    private readonly ConcurrentDictionary<CameraId, CameraRegistration> _cameras = new();
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly ReactiveSubject _results = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _statsLock = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private long _sampled;
    private long _processed;
    private long _latencyCount;
    private double _latencySumMs;
    private Task? _worker;
    private bool _initialized;

    public ObjectDetectionEngine(IObjectDetector detector, IModelProvider modelProvider,
        ILogger<ObjectDetectionEngine> log)
    {
        _detector = detector;
        _modelProvider = modelProvider;
        _log = log;
    }

    public bool IsReady => _detector.IsLoaded;
    public ExecutionProvider ActiveProvider => _detector.ActiveProvider;
    public IObservable<DetectionResult> Results => _results;

    public AnalyticsDiagnostics Diagnostics
    {
        get
        {
            long sampled = Interlocked.Read(ref _sampled);
            long processed = Interlocked.Read(ref _processed);
            double avg;
            lock (_statsLock)
                avg = _latencyCount == 0 ? 0 : _latencySumMs / _latencyCount;
            return new AnalyticsDiagnostics(
                ActiveCameras: _cameras.Count,
                FramesSampled: sampled,
                FramesProcessed: processed,
                FramesDropped: Math.Max(0, sampled - processed - QueueDepth()),
                QueueDepth: QueueDepth(),
                AverageLatencyMs: avg);
        }
    }

    public async Task InitializeAsync(AiAcceleration acceleration, CancellationToken ct)
    {
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var spec = await _modelProvider.EnsureModelAsync(ct).ConfigureAwait(false);
            await _detector.LoadAsync(spec, acceleration, ct).ConfigureAwait(false);
            _worker = Task.Run(() => WorkerLoopAsync(_shutdown.Token));
            _initialized = true;
            _log.LogInformation("Analytics engine ready on {Provider}.", _detector.ActiveProvider);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public void Attach(CameraId cameraId, IObservable<VideoFrame> frames,
        Func<AnalyticsSettings> settings, Func<bool> isActive)
    {
        Detach(cameraId);
        var reg = new CameraRegistration(new FrameSampler(settings().AnalyticsFps), settings, isActive);
        reg.Subscription = frames.Subscribe(new FrameObserver(this, cameraId, reg));
        _cameras[cameraId] = reg;
    }

    public void Detach(CameraId cameraId)
    {
        if (_cameras.TryRemove(cameraId, out var reg))
            reg.Dispose();
    }

    private void OnFrame(CameraId cameraId, CameraRegistration reg, in VideoFrame frame)
    {
        var settings = reg.Settings();
        if (!settings.Enabled || !reg.IsActive()) return;

        // Keep the sampler in step with a live FPS change.
        if (reg.Sampler.TargetFps != ClampFps(settings.AnalyticsFps))
            reg.Sampler = new FrameSampler(settings.AnalyticsFps);

        if (!reg.Sampler.ShouldSample(frame.ReceivedAt)) return;

        var buffer = Downscale(frame, DownscaleMaxSide);
        if (_queue.Writer.TryWrite(new WorkItem(cameraId, buffer, settings, frame.ReceivedAt)))
            Interlocked.Increment(ref _sampled);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_detector.IsLoaded) continue;

                try
                {
                    var sw = Stopwatch.StartNew();
                    var detections = _detector.Detect(item.Frame, item.Settings.ToDetectOptions());
                    sw.Stop();

                    Interlocked.Increment(ref _processed);
                    lock (_statsLock)
                    {
                        _latencySumMs += sw.Elapsed.TotalMilliseconds;
                        _latencyCount++;
                    }

                    _results.OnNext(new DetectionResult(
                        item.CameraId, item.Timestamp, detections, sw.Elapsed.TotalMilliseconds));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Inference failed for camera {Camera}.", item.CameraId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private int QueueDepth() => _queue.Reader.Count;

    private static int ClampFps(int fps) => fps < 1 ? 1 : fps > 30 ? 30 : fps;

    // Full-frame aspect-preserving downscale into a tightly packed BGRA buffer.
    // Because it is the WHOLE frame (no crop), detection coords normalized to
    // this buffer equal those of the original — the overlay maps cleanly.
    private static FrameBuffer Downscale(in VideoFrame f, int maxSide)
    {
        var scale = Math.Min(1f, (float)maxSide / Math.Max(f.Width, f.Height));
        var w = Math.Max(1, (int)(f.Width * scale));
        var h = Math.Max(1, (int)(f.Height * scale));
        var dst = new byte[w * h * 4];
        var sxStep = (float)f.Width / w;
        var syStep = (float)f.Height / h;

        for (var y = 0; y < h; y++)
        {
            var srcRow = Math.Min(f.Height - 1, (int)(y * syStep)) * f.Stride;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var so = srcRow + Math.Min(f.Width - 1, (int)(x * sxStep)) * 4;
                var doff = dstRow + x * 4;
                dst[doff] = f.Bgra[so];
                dst[doff + 1] = f.Bgra[so + 1];
                dst[doff + 2] = f.Bgra[so + 2];
                dst[doff + 3] = 255;
            }
        }

        return new FrameBuffer(dst, w, h, w * 4);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        foreach (var reg in _cameras.Values) reg.Dispose();
        _cameras.Clear();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _results.OnCompleted();
        _results.Dispose();
        await _detector.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private sealed record WorkItem(CameraId CameraId, FrameBuffer Frame, AnalyticsSettings Settings, DateTime Timestamp);

    private sealed class CameraRegistration : IDisposable
    {
        public CameraRegistration(FrameSampler sampler, Func<AnalyticsSettings> settings, Func<bool> isActive)
        {
            Sampler = sampler;
            Settings = settings;
            IsActive = isActive;
        }

        public FrameSampler Sampler { get; set; }
        public Func<AnalyticsSettings> Settings { get; }
        public Func<bool> IsActive { get; }
        public IDisposable? Subscription { get; set; }

        public void Dispose() => Subscription?.Dispose();
    }

    // Lightweight IObserver so we don't depend on Rx subscription extensions.
    private sealed class FrameObserver : IObserver<VideoFrame>
    {
        private readonly ObjectDetectionEngine _engine;
        private readonly CameraId _cameraId;
        private readonly CameraRegistration _reg;

        public FrameObserver(ObjectDetectionEngine engine, CameraId cameraId, CameraRegistration reg)
        {
            _engine = engine;
            _cameraId = cameraId;
            _reg = reg;
        }

        public void OnNext(VideoFrame value) => _engine.OnFrame(_cameraId, _reg, value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
