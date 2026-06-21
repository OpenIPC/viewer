using System;

namespace OpenIPC.Viewer.Core.Platform;

// Fallback sink for platforms that don't have a native IAudioOutput yet
// (currently Linux/macOS desktop until ALSA/CoreAudio land). Reports
// IsAvailable=false so the UI hides the speaker controls; every method is a
// no-op. Phase 17.2.
public sealed class NullAudioOutput : IAudioOutput
{
    public bool IsAvailable => false;
    public void Start(int sampleRate, int channels) { }
    public void Write(ReadOnlySpan<byte> pcm16) { }
    public void Stop() { }
    public void Dispose() { }
}
