using System;
using Android.Media;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Android.Platform;

// Android audio playback via AudioTrack (Phase 17.2), output counterpart to
// AndroidAudioInput. Streaming mode with a blocking Write — AudioTrack's own
// buffer provides the backpressure, so no extra ring/thread is needed.
public sealed class AndroidAudioOutput : IAudioOutput
{
    private readonly ILogger<AndroidAudioOutput> _logger;
    private readonly object _gate = new();

    private AudioTrack? _track;
    private int _channels;
    private int _sampleRate;
    private bool _available = true;

    public AndroidAudioOutput(ILogger<AndroidAudioOutput> logger) => _logger = logger;

    public bool IsAvailable => _available;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_track is not null && sampleRate == _sampleRate && channels == _channels)
                return;
            StopLocked();
            try
            {
                var channelMask = channels >= 2 ? ChannelOut.Stereo : ChannelOut.Mono;
                var minBuf = AudioTrack.GetMinBufferSize(sampleRate, channelMask, Encoding.Pcm16bit);
                if (minBuf <= 0) minBuf = sampleRate * channels * 2 / 4;

                _track = new AudioTrack.Builder()
                    .SetAudioAttributes(new AudioAttributes.Builder()!
                        .SetUsage(AudioUsageKind.Media)!
                        .SetContentType(AudioContentType.Speech)!
                        .Build()!)
                    .SetAudioFormat(new AudioFormat.Builder()!
                        .SetEncoding(Encoding.Pcm16bit)!
                        .SetSampleRate(sampleRate)!
                        .SetChannelMask(channelMask)!
                        .Build()!)
                    .SetBufferSizeInBytes(minBuf * 2)
                    .SetTransferMode(AudioTrackMode.Stream)
                    .Build();

                if (_track.State != AudioTrackState.Initialized)
                    throw new InvalidOperationException("AudioTrack failed to initialize");

                _channels = channels;
                _sampleRate = sampleRate;
                _available = true;
                _track.Play();
                _logger.LogInformation("AudioTrack playback started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AudioTrack init failed; audio output unavailable");
                _available = false;
                StopLocked();
            }
        }
    }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        AudioTrack? track;
        lock (_gate) track = _track;
        if (track is null || pcm16.IsEmpty) return;
        try
        {
            var buf = pcm16.ToArray();
            track.Write(buf, 0, buf.Length);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AudioTrack write failed");
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_track is not null)
        {
            try { _track.Stop(); } catch { }
            try { _track.Release(); } catch { }
            _track.Dispose();
            _track = null;
        }
        _channels = 0;
        _sampleRate = 0;
    }

    public void Dispose() => Stop();
}
