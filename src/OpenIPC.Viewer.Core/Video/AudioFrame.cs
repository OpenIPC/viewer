using System;

namespace OpenIPC.Viewer.Core.Video;

// One chunk of decoded, resampled audio: interleaved signed 16-bit PCM.
// The byte[] is owned by the session — a subscriber must copy it synchronously
// inside OnNext and never retain the reference past the callback (same contract
// as VideoFrame). Phase 17.1.
public readonly record struct AudioFrame(
    byte[] Pcm16,
    int SampleRate,
    int Channels,
    long PtsMicroseconds);
