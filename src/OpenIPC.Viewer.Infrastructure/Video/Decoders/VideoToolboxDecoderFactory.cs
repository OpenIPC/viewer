using System;
using System.Runtime.Versioning;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Video.Decoders;

[SupportedOSPlatform("macos")]
public sealed class VideoToolboxDecoderFactory : IHwDecoderFactory
{
    public HwAccelHint Kind => HwAccelHint.VideoToolbox;

    public HwProbeResult Probe()
    {
        // VideoToolbox ships in every macOS .NET 9 can run on (12 Monterey+).
        // No useful pre-check; codec-open is the real arbiter.
        if (!OperatingSystem.IsMacOSVersionAtLeast(12, 0))
            return HwProbeResult.Unavailable("VideoToolbox requires macOS 12 or later");
        return HwProbeResult.Ok();
    }
}
