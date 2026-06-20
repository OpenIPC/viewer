using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop.Audio;

// Linux audio playback via ALSA (Phase 17.2), the output counterpart to
// AlsaAudioInput. Opens "default" with soft-resampling so the monitor's fixed
// 48 kHz stereo S16 plays on any device. A playback thread drains the ring into
// snd_pcm_writei; Write enqueues without blocking the decode thread.
[SupportedOSPlatform("linux")]
public sealed class AlsaAudioOutput : IAudioOutput
{
    private const int SndPcmStreamPlayback = 0;
    private const int SndPcmFormatS16Le = 2;
    private const int SndPcmAccessRwInterleaved = 3;
    private const int FramesPerChunk = 480; // 10 ms @ 48 kHz

    private readonly ILogger<AlsaAudioOutput> _logger;
    private readonly object _gate = new();

    private IntPtr _pcm;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private PcmRing? _ring;
    private int _channels;
    private int _sampleRate;
    private bool _available = true;

    public AlsaAudioOutput(ILogger<AlsaAudioOutput> logger) => _logger = logger;

    public bool IsAvailable => _available;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_pcm != IntPtr.Zero && sampleRate == _sampleRate && channels == _channels)
                return;
            StopLocked();
            try
            {
                var rc = snd_pcm_open(out _pcm, "default", SndPcmStreamPlayback, 0);
                if (rc < 0) throw new InvalidOperationException($"snd_pcm_open failed: {rc}");

                rc = snd_pcm_set_params(_pcm, SndPcmFormatS16Le, SndPcmAccessRwInterleaved,
                    (uint)channels, (uint)sampleRate, 1 /*soft_resample*/, 200_000 /*latency µs*/);
                if (rc < 0) throw new InvalidOperationException($"snd_pcm_set_params failed: {rc}");

                _channels = channels;
                _sampleRate = sampleRate;
                _ring = new PcmRing(sampleRate * channels * 2 / 2); // ~0.5 s
                _available = true;

                _cts = new CancellationTokenSource();
                _thread = new Thread(() => PlaybackLoop(_cts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "alsa-render",
                };
                _thread.Start();
                _logger.LogInformation("ALSA playback started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ALSA playback init failed; audio output unavailable");
                _available = false;
                StopLocked();
            }
        }
    }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        var ring = _ring;
        if (ring is null || pcm16.IsEmpty) return;
        ring.Write(pcm16);
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        if (_thread is { IsAlive: true }) _thread.Join(TimeSpan.FromMilliseconds(500));
        _thread = null;
        _cts?.Dispose();
        _cts = null;
        if (_pcm != IntPtr.Zero)
        {
            try { snd_pcm_close(_pcm); } catch { }
            _pcm = IntPtr.Zero;
        }
        _ring = null;
        _channels = 0;
        _sampleRate = 0;
    }

    private void PlaybackLoop(CancellationToken ct)
    {
        var pcm = _pcm;
        var ring = _ring;
        if (ring is null) return;
        var chunkBytes = FramesPerChunk * _channels * 2;
        var buf = new byte[chunkBytes];

        while (!ct.IsCancellationRequested)
        {
            var got = ring.Read(buf, chunkBytes);
            if (got == 0)
            {
                Thread.Sleep(5);
                continue;
            }
            var frames = got / (_channels * 2);
            var written = snd_pcm_writei(pcm, buf, (ulong)frames);
            if (written < 0)
                snd_pcm_recover(pcm, (int)written, 1);
        }
    }

    public void Dispose() => Stop();

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);

    [DllImport("libasound.so.2")]
    private static extern long snd_pcm_writei(IntPtr pcm, byte[] buffer, ulong size);

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_close(IntPtr pcm);
}
