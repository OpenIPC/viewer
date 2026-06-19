using System;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Analytics;

// Orchestrates per-camera object detection (Phase 15.3). One shared detector
// behind a bounded queue feeds a single inference worker, so total memory and
// CPU stay bounded no matter how many cameras attach. App talks to this Core
// interface; the ONNX-backed implementation is wired per head via DI.
public interface IAnalyticsEngine : IAsyncDisposable
{
    bool IsReady { get; }
    ExecutionProvider ActiveProvider { get; }

    IObservable<DetectionResult> Results { get; }
    AnalyticsDiagnostics Diagnostics { get; }

    // Loads the model + detector. Safe to call once; subsequent calls no-op.
    Task InitializeAsync(AiAcceleration acceleration, CancellationToken ct);

    // Begin sampling frames for a camera. settings is re-read per frame so the
    // user can toggle classes/threshold live; isActive lets Smart Pause
    // (Phase 12) gate analytics for hidden/suspended tiles.
    void Attach(CameraId cameraId, IObservable<VideoFrame> frames,
        Func<AnalyticsSettings> settings, Func<bool> isActive);

    void Detach(CameraId cameraId);
}
