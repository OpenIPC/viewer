using System;
using System.Runtime.Versioning;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Video.Decoders;

// Same backend on both Apple OSes — VideoToolbox is part of the shared
// Darwin frameworks. Two SupportedOSPlatform attrs teach the analyzer to
// allow either platform's call sites.
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed class VideoToolboxDecoderFactory : IHwDecoderFactory
{
    public HwAccelHint Kind => HwAccelHint.VideoToolbox;

    public HwProbeResult Probe()
    {
        // Codec-open is the real arbiter; here we only rule out OS versions
        // .NET 9/10 won't run on anyway.
        if (OperatingSystem.IsMacOS() && !OperatingSystem.IsMacOSVersionAtLeast(12, 0))
            return HwProbeResult.Unavailable("VideoToolbox requires macOS 12 or later");
        if (OperatingSystem.IsIOS() && !OperatingSystem.IsIOSVersionAtLeast(16, 0))
            return HwProbeResult.Unavailable("VideoToolbox requires iOS 16 or later");
        return HwProbeResult.Ok();
    }
}
