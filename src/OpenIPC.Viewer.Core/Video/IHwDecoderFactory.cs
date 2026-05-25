namespace OpenIPC.Viewer.Core.Video;

// Per-platform hardware decode backend descriptor. Lives in Core so that
// Composition (Desktop) can register one without dragging FFmpeg.AutoGen into
// platform-agnostic code. The actual av_hwdevice_ctx_create plumbing happens
// inside Video, branching on Kind.
public interface IHwDecoderFactory
{
    HwAccelHint Kind { get; }

    // Cheap probe — filesystem checks (Linux) or OS-version checks. Must not
    // touch FFmpeg or allocate native resources; the codec context is what
    // ultimately tries to create the device.
    HwProbeResult Probe();
}
