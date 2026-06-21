using System;
using System.Threading;
using Android.Media;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Android.Platform;

// Android microphone capture via AudioRecord (Phase 17.6). Requests the
// backchannel's 8 kHz mono S16 directly — AudioRecord resamples from the
// hardware rate. A reader thread pumps PCM out via FrameCaptured. Requires the
// RECORD_AUDIO runtime permission (requested by the talk UI before Start).
public sealed class AndroidAudioInput : IAudioInput
{
    private const int FramesPerChunk = 160; // 20 ms @ 8 kHz

    private readonly ILogger<AndroidAudioInput> _logger;
    private readonly object _gate = new();

    private AudioRecord? _record;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _channels;
    private bool _available = true;

    public AndroidAudioInput(ILogger<AndroidAudioInput> logger) => _logger = logger;

    public bool IsAvailable => _available;
    public event Action<byte[]>? FrameCaptured;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            StopLocked();
            try
            {
                var channelConfig = channels >= 2 ? ChannelIn.Stereo : ChannelIn.Mono;
                var minBuf = AudioRecord.GetMinBufferSize(sampleRate, channelConfig, Encoding.Pcm16bit);
                if (minBuf <= 0) minBuf = FramesPerChunk * channels * 2 * 4;

                _record = new AudioRecord(AudioSource.Mic, sampleRate, channelConfig, Encoding.Pcm16bit, minBuf);
                if (_record.State != State.Initialized)
                    throw new InvalidOperationException("AudioRecord failed to initialize");

                _channels = channels;
                _available = true;
                _record.StartRecording();

                _cts = new CancellationTokenSource();
                _thread = new Thread(() => CaptureLoop(_cts.Token))
                {
                    IsBackground = true,
                    Name = "android-capture",
                };
                _thread.Start();
                _logger.LogInformation("AudioRecord capture started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AudioRecord init failed; mic unavailable");
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
        if (_record is not null)
        {
            try { _record.Stop(); } catch { }
            _record.Release();
            _record.Dispose();
            _record = null;
        }
        _channels = 0;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var record = _record;
        if (record is null) return;
        var chunkBytes = FramesPerChunk * _channels * 2;
        var buf = new byte[chunkBytes];

        while (!ct.IsCancellationRequested)
        {
            var read = record.Read(buf, 0, buf.Length);
            if (read <= 0)
            {
                Thread.Sleep(5);
                continue;
            }
            var chunk = new byte[read];
            Buffer.BlockCopy(buf, 0, chunk, 0, read);
            FrameCaptured?.Invoke(chunk);
        }
    }

    public void Dispose() => Stop();
}
