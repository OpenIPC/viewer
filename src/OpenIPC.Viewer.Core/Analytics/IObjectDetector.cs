using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Analytics;

// Cross-platform object-detection contract (Phase 15.1). The implementation
// lives in OpenIPC.Viewer.Analytics on ONNX Runtime and is wired per head via
// DI; App/Core only see this interface. Detect is synchronous and meant to run
// on a dedicated worker thread (the sampling stage owns the threading).
public interface IObjectDetector : IAsyncDisposable
{
    bool IsLoaded { get; }

    // What execution provider actually initialized (after the fallback chain).
    ExecutionProvider ActiveProvider { get; }

    Task LoadAsync(ModelSpec model, AiAcceleration acceleration, CancellationToken ct);

    IReadOnlyList<Detection> Detect(FrameBuffer frame, DetectOptions options);
}
