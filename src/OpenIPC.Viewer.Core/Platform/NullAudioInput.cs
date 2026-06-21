using System;

namespace OpenIPC.Viewer.Core.Platform;

// Fallback capture for platforms without a native IAudioInput yet. IsAvailable
// false → the push-to-talk button hides; every method is a no-op. Phase 17.6.
public sealed class NullAudioInput : IAudioInput
{
    public bool IsAvailable => false;
    public void Start(int sampleRate, int channels) { }
    public void Stop() { }
#pragma warning disable CS0067 // never raised — no device
    public event Action<byte[]>? FrameCaptured;
#pragma warning restore CS0067
    public void Dispose() { }
}
