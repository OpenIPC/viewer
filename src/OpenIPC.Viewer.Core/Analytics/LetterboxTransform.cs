using System;

namespace OpenIPC.Viewer.Core.Analytics;

// Aspect-preserving letterbox from a source frame into the square model input,
// with centered padding. The detector uses Compute to know the scale/pad when
// building the input tensor, and MapToSource to turn an input-pixel box back
// into a source-normalized Detection. Pure + allocation-free so it unit-tests
// independently of ONNX Runtime (Phase 15 note: box coords must be mapped back
// through the letterbox, accounting for aspect ratio).
public readonly struct LetterboxTransform
{
    public LetterboxTransform(int sourceWidth, int sourceHeight, int inputWidth, int inputHeight)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        Scale = Math.Min((float)inputWidth / sourceWidth, (float)inputHeight / sourceHeight);
        PadX = (inputWidth - sourceWidth * Scale) / 2f;
        PadY = (inputHeight - sourceHeight * Scale) / 2f;
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int InputWidth { get; }
    public int InputHeight { get; }

    // Source pixel -> input pixel; PadX/PadY are the centered black bars.
    public float Scale { get; }
    public float PadX { get; }
    public float PadY { get; }

    // Map an input-pixel top-left box (x,y,w,h) to a source-normalized 0..1
    // Detection, clamped to the frame.
    public Detection MapToSource(int classId, string className, float confidence,
        float inX, float inY, float inW, float inH)
    {
        var sx = (inX - PadX) / Scale / SourceWidth;
        var sy = (inY - PadY) / Scale / SourceHeight;
        var sw = inW / Scale / SourceWidth;
        var sh = inH / Scale / SourceHeight;

        // Clamp to [0,1] while keeping the box inside the frame.
        var x0 = Clamp01(sx);
        var y0 = Clamp01(sy);
        var x1 = Clamp01(sx + sw);
        var y1 = Clamp01(sy + sh);
        return new Detection(classId, className, confidence, x0, y0, x1 - x0, y1 - y0);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
