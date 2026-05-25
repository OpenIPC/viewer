using System;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Video.Pipeline;

// Resolves the requested HwAccelHint against the platform-specific
// IHwDecoderFactory and configures the AVCodecContext. Two halves:
//
//   Resolve(): pure decision logic, unit-testable in Core.Tests.
//   Configure(): native side — av_hwdevice_ctx_create + get_format wiring.
//
// Resolve never throws and always returns a valid hint (None on any failure
// or mismatch). Configure returns false on failure and leaves the codec
// context untouched, so the caller can transparently fall back to software.
public static class HwAccelSelector
{
    public static HwAccelHint Resolve(HwAccelHint requested, IHwDecoderFactory? factory, ILogger logger)
    {
        if (requested == HwAccelHint.None)
            return HwAccelHint.None;

        if (requested == HwAccelHint.Auto)
        {
            if (factory is null)
            {
                logger.LogInformation("HW accel Auto: no factory registered, using software");
                return HwAccelHint.None;
            }
            var probe = factory.Probe();
            if (!probe.Available)
            {
                logger.LogInformation("HW accel Auto: {Kind} unavailable ({Reason}), using software",
                    factory.Kind, probe.Reason);
                return HwAccelHint.None;
            }
            return factory.Kind;
        }

        // Explicit hint
        if (factory is null || factory.Kind != requested)
        {
            logger.LogWarning("HW accel {Requested} requested but factory provides {Have}; using software",
                requested, factory?.Kind);
            return HwAccelHint.None;
        }
        var explicitProbe = factory.Probe();
        if (!explicitProbe.Available)
        {
            logger.LogWarning("HW accel {Kind} unavailable ({Reason}); using software",
                factory.Kind, explicitProbe.Reason);
            return HwAccelHint.None;
        }
        return factory.Kind;
    }

    public static (AVHWDeviceType DeviceType, AVPixelFormat HwPixFmt) MapToFfmpeg(HwAccelHint hint) =>
        hint switch
        {
            HwAccelHint.D3d11Va => (AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVPixelFormat.AV_PIX_FMT_D3D11),
            HwAccelHint.VaApi => (AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVPixelFormat.AV_PIX_FMT_VAAPI),
            HwAccelHint.VideoToolbox => (AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX, AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX),
            _ => throw new ArgumentOutOfRangeException(nameof(hint), hint, "Not a hardware hint"),
        };
}
