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

    // Decoded + resampled PCM audio (Phase 17.1). Only emits when the session
    // was created with VideoSessionOptions.EnableAudio; otherwise it completes
    // with no items. The camera may carry no audio track at all — in that case
    // it is also silent, never faulting.
    IObservable<AudioFrame> AudioFrames { get; }

    Task StartAsync(CancellationToken ct);
    Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct);

    // Toggle audio decode on a live session without tearing it down (Phase 17).
    // Lets a grid tile start/stop listening with no video blip — the audio
    // packets are already demuxed, this only spins the audio decoder up/down.
    // Idempotent; sticky across auto-reconnects. No-op if the stream has no audio.
    void SetAudioEnabled(bool enabled);

    // Smart Pause (Phase 12.1): stop decoding without tearing the session down,
    // so a hidden/minimized tile stops burning CPU while keeping its last frame
    // for an instant resume. Both are idempotent no-ops if not started.
    void PauseDecode();
    void Resume();
}
