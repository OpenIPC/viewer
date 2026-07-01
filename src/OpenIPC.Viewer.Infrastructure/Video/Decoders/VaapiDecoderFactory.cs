using System.IO;
using System.Runtime.InteropServices;
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
        }
        catch (System.Exception ex)
        {
            return HwProbeResult.Unavailable($"{RenderNode} not readable: {ex.Message}");
        }

        // The bundled BtbN FFmpeg links libva via Implib.so lazy shims built
        // against libva >= 2.21. If the system libva.so.2 is missing a symbol
        // the shim resolves (vaMapBuffer2, added in 2.21) — or is missing
        // entirely — the generated trampoline assert()s and abort()s the whole
        // process as soon as VAAPI decode transfers a frame. That native abort
        // cannot be caught from managed code, so verify up front and fall back
        // to software decode instead.
        if (!NativeLibrary.TryLoad("libva.so.2", out var libva))
            return HwProbeResult.Unavailable("libva.so.2 not found");
        try
        {
            if (!NativeLibrary.TryGetExport(libva, "vaMapBuffer2", out _))
                return HwProbeResult.Unavailable(
                    "system libva older than 2.21 (no vaMapBuffer2); bundled FFmpeg would abort");
        }
        finally
        {
            NativeLibrary.Free(libva);
        }

        return HwProbeResult.Ok();
    }
}
