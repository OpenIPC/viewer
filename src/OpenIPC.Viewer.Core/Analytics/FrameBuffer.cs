namespace OpenIPC.Viewer.Core.Analytics;

// A single frame handed to the detector. Pixels are BGRA8888 — the same layout
// the video pipeline already produces (VideoFrame.Bgra) — so the sampling stage
// can forward decoded frames without a colour conversion. The detector does the
// letterbox + normalize to the model input internally; callers pass whatever
// resolution they have (full frame or a cheap downscale).
public sealed class FrameBuffer
{
    public FrameBuffer(byte[] bgra, int width, int height, int stride)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
}
