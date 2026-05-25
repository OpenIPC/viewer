using System;
using System.Runtime.Versioning;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Video.Decoders;

[SupportedOSPlatform("windows")]
public sealed class D3d11VaDecoderFactory : IHwDecoderFactory
{
    public HwAccelHint Kind => HwAccelHint.D3d11Va;

    public HwProbeResult Probe()
    {
        // D3D11VA requires Windows 8 / Server 2012+. Real device creation happens
        // when FFmpeg calls av_hwdevice_ctx_create — failures there fall back to
        // software in HwAccelSelector.
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return HwProbeResult.Unavailable("D3D11VA requires Windows 8 or later");
        return HwProbeResult.Ok();
    }
}
