using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Video;

public interface IVideoSession : IAsyncDisposable
{
    SessionState State { get; }
    string? LastError { get; }

    IObservable<SessionState> StateChanged { get; }
    IObservable<VideoFrame> Frames { get; }
    IObservable<SessionTelemetry> Telemetry { get; }

    Task StartAsync(CancellationToken ct);
    Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct);

    // Smart Pause (Phase 12.1): stop decoding without tearing the session down,
    // so a hidden/minimized tile stops burning CPU while keeping its last frame
    // for an instant resume. Both are idempotent no-ops if not started.
    void PauseDecode();
    void Resume();
}
