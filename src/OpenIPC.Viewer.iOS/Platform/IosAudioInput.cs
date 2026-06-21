using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.iOS.Platform;

// iOS microphone capture via AVAudioEngine (Phase 17.6). The input node's tap
// delivers the hardware format, so an AVAudioConverter downmixes/resamples to the
// backchannel's 8 kHz mono S16. Requires NSMicrophoneUsageDescription in
// Info.plist + a granted record permission (the talk UI requests it).
// Compiles on the iOS head only; unverified without a device.
[SupportedOSPlatform("ios")]
public sealed class IosAudioInput : IAudioInput
{
    private readonly ILogger<IosAudioInput> _logger;
    private readonly object _gate = new();

    private AVAudioEngine? _engine;
    private AVAudioConverter? _converter;
    private AVAudioFormat? _outFormat;
    private bool _available = true;

    public IosAudioInput(ILogger<IosAudioInput> logger) => _logger = logger;

    public bool IsAvailable => _available;
    public event Action<byte[]>? FrameCaptured;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            StopLocked();
            try
            {
                var session = AVAudioSession.SharedInstance();
                session.SetCategory(AVAudioSessionCategory.PlayAndRecord);
                session.SetActive(true, out _);

                _engine = new AVAudioEngine();
                var input = _engine.InputNode;
                var inputFormat = input.GetBusOutputFormat(0);

                _outFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, (uint)channels, interleaved: true);
                _converter = new AVAudioConverter(inputFormat, _outFormat);

                input.InstallTapOnBus(0, 4096, inputFormat, OnTap);

                _engine.Prepare();
                if (!_engine.StartAndReturnError(out var err))
                    throw new InvalidOperationException($"AVAudioEngine start failed: {err}");

                _available = true;
                _logger.LogInformation("AVAudioEngine capture started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AVAudioEngine capture init failed; mic unavailable");
                _available = false;
                StopLocked();
            }
        }
    }

    private void OnTap(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        var converter = _converter;
        var outFormat = _outFormat;
        if (converter is null || outFormat is null) return;

        try
        {
            // Output capacity scaled by the rate ratio (+1 for rounding).
            var ratio = outFormat.SampleRate / buffer.Format.SampleRate;
            var capacity = (uint)(buffer.FrameLength * ratio) + 1;
            using var outBuf = new AVAudioPcmBuffer(outFormat, capacity);

            var status = converter.ConvertToBuffer(outBuf, out var convErr, (uint packets, out AVAudioConverterInputStatus inStatus) =>
            {
                inStatus = AVAudioConverterInputStatus.HaveData;
                return buffer;
            });
            if (status == AVAudioConverterOutputStatus.Error || convErr is not null) return;

            var frames = (int)outBuf.FrameLength;
            if (frames <= 0) return;

            var channelPtrs = outBuf.Int16ChannelData;
            if (channelPtrs == IntPtr.Zero) return;
            var channel0 = Marshal.ReadIntPtr(channelPtrs);
            if (channel0 == IntPtr.Zero) return;

            var bytes = frames * (int)outFormat.ChannelCount * 2;
            var chunk = new byte[bytes];
            Marshal.Copy(channel0, chunk, 0, bytes);
            FrameCaptured?.Invoke(chunk);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AVAudioEngine tap failed");
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_engine is not null)
        {
            try
            {
                _engine.InputNode.RemoveTapOnBus(0);
                _engine.Stop();
            }
            catch { /* tearing down */ }
            _engine.Dispose();
            _engine = null;
        }
        _converter?.Dispose();
        _converter = null;
        _outFormat?.Dispose();
        _outFormat = null;
    }

    public void Dispose() => Stop();
}
