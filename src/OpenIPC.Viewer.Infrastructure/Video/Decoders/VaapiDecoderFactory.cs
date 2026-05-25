using System.IO;
using System.Runtime.Versioning;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Video.Decoders;

[SupportedOSPlatform("linux")]
public sealed class VaapiDecoderFactory : IHwDecoderFactory
{
    private const string RenderNode = "/dev/dri/renderD128";

    public HwAccelHint Kind => HwAccelHint.VaApi;

    public HwProbeResult Probe()
    {
        // /dev/dri/renderD128 is the standard render-node path. Missing on
        // headless servers or boxes without a usable GPU; absent or unreadable
        // means we should not try VAAPI (codec-open would fail noisily).
        if (!File.Exists(RenderNode))
            return HwProbeResult.Unavailable($"{RenderNode} not found");

        try
        {
            using var _ = File.OpenRead(RenderNode);
            return HwProbeResult.Ok();
        }
        catch (System.Exception ex)
        {
            return HwProbeResult.Unavailable($"{RenderNode} not readable: {ex.Message}");
        }
    }
}
