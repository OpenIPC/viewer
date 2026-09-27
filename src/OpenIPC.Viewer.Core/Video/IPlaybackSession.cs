using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Video;

// A playback session over a recorded file (Phase 16). Extends the live
// IVideoSession (same Frames/State/Telemetry surface, so the existing
// RtspVideoView renders it unchanged) with transport controls: a known
// Duration, an observable Position, play/pause, and keyframe seek.
//
// Note on pause semantics: Play()/Pause() are *transport* — they advance or
// freeze the playhead. The inherited PauseDecode()/Resume() remain the Smart
// Pause hooks (tile hidden); a file player wires them to the same freeze.
public interface IPlaybackSession : IVideoSession
{
    // Total length of the file, probed on StartAsync. TimeSpan.Zero until known.
    TimeSpan Duration { get; }

    // Current playhead position, also pushed through PositionChanged.
    TimeSpan Position { get; }

    bool IsPaused { get; }

    // Playback speed multiplier (1 = real time). Presentation re-anchors on
    // change so the playhead doesn't jump.
    double Rate { get; set; }

    IObservable<TimeSpan> PositionChanged { get; }

    void Play();
    void Pause();

    // Seek to the nearest keyframe at or before the requested position, then
    // decode forward to it. Idempotent while seeking is in flight (latest wins).
    Task SeekAsync(TimeSpan position, CancellationToken ct);

    // Pause and move exactly one frame. Forward decodes the next frame; backward
    // re-seeks to the previous one (keyframe + decode forward), so it costs up
    // to a GOP of decoding.
    void StepFrame(bool forward);
}
