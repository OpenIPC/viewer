using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop.Audio;

// Linux microphone capture via ALSA (Phase 17.6). The minimal common denominator
// per the phase plan — opens the "default" PCM with soft-resampling on, so the
// requested 8 kHz mono S16 is delivered regardless of the device's native rate.
// A capture thread reads interleaved frames and raises FrameCaptured.
[SupportedOSPlatform("linux")]
public sealed class AlsaAudioInput : IAudioInput
{
    private const int SndPcmStreamCapture = 1;
    private const int SndPcmFormatS16Le = 2;
    private const int SndPcmAccessRwInterleaved = 3;
    private const int FramesPerChunk = 160; // 20 ms @ 8 kHz

    private readonly ILogger<AlsaAudioInput> _logger;
    private readonly object _gate = new();

    private IntPtr _pcm;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _channels;
    private bool _available = true;

    public AlsaAudioInput(ILogger<AlsaAudioInput> logger) => _logger = logger;

    public bool IsAvailable => _available;
    public event Action<byte[]>? FrameCaptured;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            StopLocked();
            try
            {
                var rc = snd_pcm_open(out _pcm, "default", SndPcmStreamCapture, 0);
                if (rc < 0) throw new InvalidOperationException($"snd_pcm_open failed: {rc}");

                rc = snd_pcm_set_params(_pcm, SndPcmFormatS16Le, SndPcmAccessRwInterleaved,
                    (uint)channels, (uint)sampleRate, 1 /*soft_resample*/, 100_000 /*latency µs*/);
                if (rc < 0) throw new InvalidOperationException($"snd_pcm_set_params failed: {rc}");

                _channels = channels;
                _available = true;
                _cts = new CancellationTokenSource();
                _thread = new Thread(() => CaptureLoop(_cts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "alsa-capture",
                };
                _thread.Start();
                _logger.LogInformation("ALSA capture started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ALSA capture init failed; mic unavailable");
                _available = false;
                StopLocked();
            }
        }
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
            try { snd_pcm_close(_pcm); } catch { /* tearing down */ }
            _pcm = IntPtr.Zero;
        }
        _channels = 0;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var pcm = _pcm;
        var channels = _channels;
        var buf = new byte[FramesPerChunk * channels * 2];
        while (!ct.IsCancellationRequested)
        {
            var frames = snd_pcm_readi(pcm, buf, FramesPerChunk);
            if (frames < 0)
            {
                // Underrun/overrun (-EPIPE) etc. — try to recover and continue.
                snd_pcm_recover(pcm, (int)frames, 1);
                continue;
            }
            if (frames == 0) continue;

            var bytes = (int)frames * channels * 2;
            var chunk = new byte[bytes];
            Buffer.BlockCopy(buf, 0, chunk, 0, bytes);
            FrameCaptured?.Invoke(chunk);
        }
    }

    public void Dispose() => Stop();

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);

    [DllImport("libasound.so.2")]
    private static extern long snd_pcm_readi(IntPtr pcm, byte[] buffer, ulong size);

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

    [DllImport("libasound.so.2")]
    private static extern int snd_pcm_close(IntPtr pcm);
}
