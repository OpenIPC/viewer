using System;

namespace OpenIPC.Viewer.Core.Platform;

// Native PCM playback sink, implemented per platform (WASAPI on Windows, ALSA
// on Linux, CoreAudio on macOS, AudioTrack on Android, …) and wired via DI like
// IFileSystem / IHwDecoderFactory. FFmpeg is NOT involved here — it only decodes
// the compressed audio to PCM; getting PCM into the speakers is the OS's job.
// Phase 17.2.
//
// Contract: feed interleaved signed-16-bit PCM. The sink owns a small ring
// buffer and drops the oldest data on overflow — for live monitoring low latency
// beats perfect continuity. On a platform with no output device the
// implementation reports IsAvailable=false and every call is a safe no-op so the
// "listen" feature simply hides instead of crashing.
public interface IAudioOutput : IDisposable
{
    // False when there is no usable output device (or no native implementation
    // for this platform). The UI uses this to gate the speaker controls.
    bool IsAvailable { get; }

    // (Re)open the device for the given PCM format. Calling Start again with a
    // different format reconfigures the device. Idempotent for the same format.
    void Start(int sampleRate, int channels);

    // Enqueue interleaved S16 PCM for playback. Non-blocking; oldest samples are
    // dropped if the ring buffer is full. No-op before Start / when unavailable.
    void Write(ReadOnlySpan<byte> pcm16);

    // Stop playback and release the device. Safe to call when already stopped.
    void Stop();
}
