using System;

namespace OpenIPC.Viewer.Core.Platform;

// Native microphone capture, per platform like IAudioOutput (WASAPI capture on
// Windows, etc.). Feeds the push-to-talk backchannel (Phase 17.6). Captures
// interleaved signed-16 PCM at the requested format; for backchannel that's
// 8 kHz mono, which the platform engine resamples to from the device's mix
// format. On a machine with no input device IsAvailable=false and the talk
// button hides.
public interface IAudioInput : IDisposable
{
    // False when there is no usable capture device / no native implementation.
    bool IsAvailable { get; }

    // Open the device and start delivering FrameCaptured callbacks. Calling Start
    // again with a different format reconfigures it.
    void Start(int sampleRate, int channels);

    // Stop capture and release the device. Safe when already stopped.
    void Stop();

    // Raised on a capture thread with a chunk of interleaved S16 PCM. The buffer
    // is freshly allocated per callback, so a handler may retain it.
    event Action<byte[]>? FrameCaptured;
}
