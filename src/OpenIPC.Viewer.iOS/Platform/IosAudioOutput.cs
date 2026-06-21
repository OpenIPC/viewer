using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AVFoundation;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.iOS.Platform;

// iOS audio playback via AVAudioEngine + AVAudioPlayerNode (Phase 17.2), output
// counterpart to IosAudioInput. Each decoded chunk is wrapped in an
// AVAudioPcmBuffer and scheduled on the player node. Compiles on the iOS head
// only; unverified without a device.
[SupportedOSPlatform("ios")]
public sealed class IosAudioOutput : IAudioOutput
{
    private readonly ILogger<IosAudioOutput> _logger;
    private readonly object _gate = new();

    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioFormat? _format;
    private int _channels;
    private int _sampleRate;
    private bool _available = true;

    public IosAudioOutput(ILogger<IosAudioOutput> logger) => _logger = logger;

    public bool IsAvailable => _available;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_engine is not null && sampleRate == _sampleRate && channels == _channels)
                return;
            StopLocked();
            try
            {
                var session = AVAudioSession.SharedInstance();
                // PlayAndRecord + speaker so listen and talk can coexist.
                session.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker);
                session.SetActive(true, out _);

                _format = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, (uint)channels, interleaved: true);
                _engine = new AVAudioEngine();
                _player = new AVAudioPlayerNode();
                _engine.AttachNode(_player);
                _engine.Connect(_player, _engine.MainMixerNode, _format);

                _engine.Prepare();
                if (!_engine.StartAndReturnError(out var err))
                    throw new InvalidOperationException($"AVAudioEngine start failed: {err}");
                _player.Play();

                _channels = channels;
                _sampleRate = sampleRate;
                _available = true;
                _logger.LogInformation("AVAudioEngine playback started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AVAudioEngine playback init failed; audio output unavailable");
                _available = false;
                StopLocked();
            }
        }
    }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        AVAudioPlayerNode? player;
        AVAudioFormat? format;
        int channels;
        lock (_gate)
        {
            player = _player;
            format = _format;
            channels = _channels;
        }
        if (player is null || format is null || pcm16.IsEmpty) return;

        try
        {
            var bytes = pcm16.Length;
            var frames = (uint)(bytes / (channels * 2));
            if (frames == 0) return;

            var buffer = new AVAudioPcmBuffer(format, frames) { FrameLength = frames };
            var channelData = buffer.Int16ChannelData;
            if (channelData == IntPtr.Zero) return;
            var dst = Marshal.ReadIntPtr(channelData); // interleaved → single plane
            if (dst == IntPtr.Zero) return;

            var tmp = pcm16.ToArray();
            Marshal.Copy(tmp, 0, dst, bytes);
            player.ScheduleBuffer(buffer, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AVAudioEngine schedule failed");
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_player is not null)
        {
            try { _player.Stop(); } catch { }
            _player.Dispose();
            _player = null;
        }
        if (_engine is not null)
        {
            try { _engine.Stop(); } catch { }
            _engine.Dispose();
            _engine = null;
        }
        _format?.Dispose();
        _format = null;
        _channels = 0;
        _sampleRate = 0;
    }

    public void Dispose() => Stop();
}
