using System.Diagnostics;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Analytics;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Analytics.Tests;

// Engine wiring (Phase 15.3) with a fake detector — no ONNX native loaded.
// Verifies attach -> sample -> worker -> detect -> Results, and that ForceCpu
// surfaces the CPU provider.
public sealed class ObjectDetectionEngineTests
{
    [Fact]
    public async Task Attach_RunsDetector_AndPublishesResult()
    {
        var detector = new FakeDetector();
        await using var engine = new ObjectDetectionEngine(
            detector, new FakeModelProvider(), NullLogger<ObjectDetectionEngine>.Instance);
        await engine.InitializeAsync(AiAcceleration.ForceCpu, CancellationToken.None);

        Assert.True(engine.IsReady);
        Assert.Equal(ExecutionProvider.Cpu, engine.ActiveProvider);

        var cameraId = new CameraId(Guid.NewGuid());
        using var frames = new Subject<VideoFrame>();
        DetectionResult? received = null;
        using var sub = engine.Results.Subscribe(r => received = r);

        engine.Attach(cameraId, frames,
            () => new AnalyticsSettings(Enabled: true, AnalyticsFps: 30),
            () => true);

        frames.OnNext(NewFrame());

        var sw = Stopwatch.StartNew();
        while (received is null && sw.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(20);

        Assert.NotNull(received);
        Assert.Equal(cameraId, received!.CameraId);
        Assert.Single(received.Detections);
        Assert.True(detector.DetectCalls >= 1);
    }

    [Fact]
    public async Task Detach_StopsForwardingFrames()
    {
        var detector = new FakeDetector();
        await using var engine = new ObjectDetectionEngine(
            detector, new FakeModelProvider(), NullLogger<ObjectDetectionEngine>.Instance);
        await engine.InitializeAsync(AiAcceleration.ForceCpu, CancellationToken.None);

        var cameraId = new CameraId(Guid.NewGuid());
        using var frames = new Subject<VideoFrame>();
        engine.Attach(cameraId, frames, () => new AnalyticsSettings(Enabled: true, AnalyticsFps: 30), () => true);
        engine.Detach(cameraId);

        var before = detector.DetectCalls;
        frames.OnNext(NewFrame());
        await Task.Delay(150);

        Assert.Equal(before, detector.DetectCalls);
    }

    private static VideoFrame NewFrame() =>
        new(new byte[64 * 64 * 4], 64, 64, 64 * 4, 0, DateTime.UtcNow);

    private sealed class FakeDetector : IObjectDetector
    {
        private int _detectCalls;
        public int DetectCalls => _detectCalls;
        public bool IsLoaded { get; private set; }
        public ExecutionProvider ActiveProvider { get; private set; } = ExecutionProvider.Cpu;

        public Task LoadAsync(ModelSpec model, AiAcceleration acceleration, CancellationToken ct)
        {
            IsLoaded = true;
            ActiveProvider = ExecutionProvider.Cpu;
            return Task.CompletedTask;
        }

        public IReadOnlyList<Detection> Detect(FrameBuffer frame, DetectOptions options)
        {
            Interlocked.Increment(ref _detectCalls);
            return new[] { new Detection(0, "person", 0.9f, 0.1f, 0.1f, 0.2f, 0.2f) };
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        public Task<ModelSpec> EnsureModelAsync(CancellationToken ct) =>
            Task.FromResult(ModelSpec.YoloxTiny("dummy.onnx"));
    }
}
