using System;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Core.Video;

// The single place that turns a session's decoded audio into sound (Phase 17.3).
// Enforces the "one audio source" policy: at most one camera is attached at a
// time, so attaching a second silences the first. Owns mute + volume, applying
// them in software before handing PCM to the native IAudioOutput — that keeps
// the gain logic testable here instead of duplicated across five platform sinks.
//
// Default is muted: a freshly opened camera must never blast audio unprompted.
public sealed class AudioMonitor : IDisposable
{
    private readonly IAudioOutput _output;
    private readonly object _gate = new();

    private IDisposable? _subscription;
    private CameraId? _attachedTo;
    private int _sampleRate;
    private int _channels;

    private bool _muted = true;
    private float _volume = 1.0f;

    public AudioMonitor(IAudioOutput output)
    {
        _output = output;
    }

    // Raised (off the caller's thread) whenever Muted / Volume / the attached
    // camera changes, so a view-model can re-surface the speaker button state.
    public event EventHandler? Changed;

    // No native sink / no output device → the whole feature hides in the UI.
    public bool IsAvailable => _output.IsAvailable;

    public CameraId? AttachedCamera
    {
        get { lock (_gate) return _attachedTo; }
    }

    public bool Muted
    {
        get { lock (_gate) return _muted; }
        set
        {
            lock (_gate)
            {
                if (_muted == value) return;
                _muted = value;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Linear gain 0..1. Independent of Muted — toggling mute does not lose the
    // user's chosen level.
    public float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            lock (_gate)
            {
                if (Math.Abs(_volume - clamped) < 0.0001f) return;
                _volume = clamped;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Start routing this camera's audio to the speakers. Replaces any previous
    // source (one-source policy). No-op if the same camera is already attached.
    public void Attach(IVideoSession session, CameraId cameraId)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (!_output.IsAvailable) return;

        lock (_gate)
        {
            if (_attachedTo == cameraId && _subscription is not null) return;
            DetachLocked();
            _attachedTo = cameraId;
            _subscription = session.AudioFrames.Subscribe(new FrameObserver(this));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Stop routing audio. If a specific camera is passed, only detach when that
    // camera is the current source (avoids a stale page tearing down sound the
    // user just started on another camera).
    public void Detach(CameraId? cameraId = null)
    {
        lock (_gate)
        {
            if (_attachedTo is null) return;
            if (cameraId is not null && _attachedTo != cameraId) return;
            DetachLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void DetachLocked()
    {
        _subscription?.Dispose();
        _subscription = null;
        _attachedTo = null;
        _sampleRate = 0;
        _channels = 0;
        _output.Stop();
    }

    private void OnFrame(AudioFrame frame)
    {
        bool muted;
        float volume;
        lock (_gate)
        {
            if (_subscription is null) return; // detached mid-flight
            if (frame.SampleRate != _sampleRate || frame.Channels != _channels)
            {
                _output.Start(frame.SampleRate, frame.Channels);
                _sampleRate = frame.SampleRate;
                _channels = frame.Channels;
            }
            muted = _muted;
            volume = _volume;
        }

        // Muted: drop the data entirely rather than pushing silence — keeps the
        // sink idle and unmute is instant (next frame opens the gate).
        if (muted || volume <= 0f) return;

        var pcm = frame.Pcm16;
        if (volume >= 0.999f)
        {
            _output.Write(pcm);
            return;
        }

        // Software gain on the S16 samples. Rent-free small copy — audio buffers
        // are tiny relative to video frames.
        var scaled = ApplyGain(pcm, volume);
        _output.Write(scaled);
    }

    // Software linear gain on interleaved S16 PCM. Public for unit testing.
    public static byte[] ApplyGain(byte[] pcm16, float volume)
    {
        var outBuf = new byte[pcm16.Length];
        var samples = pcm16.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var lo = pcm16[i * 2];
            var hi = pcm16[i * 2 + 1];
            var s = (short)(lo | (hi << 8));
            var v = (int)Math.Round(s * volume);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            outBuf[i * 2] = (byte)(v & 0xFF);
            outBuf[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return outBuf;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachLocked();
        }
    }

    // Minimal IObserver so Core stays free of a System.Reactive package dep
    // (IObservable/IObserver themselves live in System).
    private sealed class FrameObserver : IObserver<AudioFrame>
    {
        private readonly AudioMonitor _owner;
        public FrameObserver(AudioMonitor owner) => _owner = owner;
        public void OnNext(AudioFrame value) => _owner.OnFrame(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
