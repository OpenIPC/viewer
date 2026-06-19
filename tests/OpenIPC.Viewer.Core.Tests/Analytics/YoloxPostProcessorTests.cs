using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Core.Tests.Analytics;

// Pure YOLOX decode + NMS + class-filter (Phase 15.1). Builds synthetic output
// tensors over a small 64×64 / strides[16,32] geometry so the maths is checkable
// by hand. Layout per anchor: [cx, cy, w, h, obj, cls0, cls1].
public sealed class YoloxPostProcessorTests
{
    private const int Input = 64;
    private static readonly int[] Strides = { 16, 32 };
    private const int Classes = 2;
    private const int Channels = 5 + Classes; // 7

    [Fact]
    public void AnchorCount_MatchesYoloxTiny416()
    {
        // (52² + 26² + 13²) = 3549 anchors for 416 input, strides 8/16/32.
        Assert.Equal(3549, YoloxPostProcessor.AnchorCount(416, 416, new[] { 8, 16, 32 }));
    }

    [Fact]
    public void AnchorCount_MatchesTestGeometry()
    {
        // 64/16 = 4 → 16, 64/32 = 2 → 4, total 20.
        Assert.Equal(20, YoloxPostProcessor.AnchorCount(Input, Input, Strides));
    }

    [Fact]
    public void Process_DecodesGridAndStride_ProducesExpectedBox()
    {
        var output = NewOutput();
        // stride-16 block, grid (gx=2, gy=1) → anchor index 1*4 + 2 = 6.
        SetAnchor(output, index: 6, rawX: 0.5f, rawY: 0.5f, rawW: 0f, rawH: 0f,
            obj: 1f, classProbs: (0.9f, 0f));

        var result = YoloxPostProcessor.Process(
            output, Classes, Input, Input, Strides, gridDecode: true,
            new DetectOptions(ConfidenceThreshold: 0.5f));

        var det = Assert.Single(result);
        Assert.Equal(0, det.ClassId);
        Assert.Equal(0.9f, det.Confidence, 3);
        // cx=(0.5+2)*16=40, cy=(0.5+1)*16=24, w=h=exp(0)*16=16 → top-left (32,16).
        Assert.Equal(32f, det.X, 2);
        Assert.Equal(16f, det.Y, 2);
        Assert.Equal(16f, det.Width, 2);
        Assert.Equal(16f, det.Height, 2);
    }

    [Fact]
    public void Process_ClassFilter_DropsUnselectedClass()
    {
        var output = NewOutput();
        SetAnchor(output, index: 6, 0.5f, 0.5f, 0f, 0f, obj: 1f, classProbs: (0.9f, 0f));

        var result = YoloxPostProcessor.Process(
            output, Classes, Input, Input, Strides, gridDecode: true,
            new DetectOptions(ConfidenceThreshold: 0.5f, ClassFilter: new[] { 1 }));

        Assert.Empty(result); // best class is 0, filter only keeps class 1
    }

    [Fact]
    public void Process_BelowThreshold_IsDropped()
    {
        var output = NewOutput();
        SetAnchor(output, index: 6, 0.5f, 0.5f, 0f, 0f, obj: 0.5f, classProbs: (0.4f, 0f)); // score 0.2

        var result = YoloxPostProcessor.Process(
            output, Classes, Input, Input, Strides, gridDecode: true,
            new DetectOptions(ConfidenceThreshold: 0.5f));

        Assert.Empty(result);
    }

    [Fact]
    public void Process_Nms_SuppressesOverlappingSameClass()
    {
        var output = NewOutput();
        // Two anchors decoding to the same box, same class, different scores.
        SetAnchor(output, index: 6, rawX: 0.5f, rawY: 0.5f, rawW: 0f, rawH: 0f,
            obj: 1f, classProbs: (0.9f, 0f)); // cx40 cy24
        // grid (gx=3, gy=1) → anchor 7; rawX=-0.5 → cx=(-0.5+3)*16=40, same box.
        SetAnchor(output, index: 7, rawX: -0.5f, rawY: 0.5f, rawW: 0f, rawH: 0f,
            obj: 1f, classProbs: (0.8f, 0f));

        var result = YoloxPostProcessor.Process(
            output, Classes, Input, Input, Strides, gridDecode: true,
            new DetectOptions(ConfidenceThreshold: 0.5f, NmsIouThreshold: 0.45f));

        var det = Assert.Single(result);
        Assert.Equal(0.9f, det.Confidence, 3); // the higher-scoring box survives
    }

    [Fact]
    public void Process_DifferentClasses_AreNotSuppressed()
    {
        var output = NewOutput();
        SetAnchor(output, index: 6, 0.5f, 0.5f, 0f, 0f, obj: 1f, classProbs: (0.9f, 0f));   // class0
        SetAnchor(output, index: 7, -0.5f, 0.5f, 0f, 0f, obj: 1f, classProbs: (0f, 0.85f)); // class1, same box

        var result = YoloxPostProcessor.Process(
            output, Classes, Input, Input, Strides, gridDecode: true,
            new DetectOptions(ConfidenceThreshold: 0.5f, NmsIouThreshold: 0.45f));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Process_RejectsMismatchedOutputLength()
    {
        Assert.Throws<ArgumentException>(() => YoloxPostProcessor.Process(
            new float[5], Classes, Input, Input, Strides, gridDecode: true, new DetectOptions()));
    }

    private static float[] NewOutput() =>
        new float[YoloxPostProcessor.AnchorCount(Input, Input, Strides) * Channels];

    private static void SetAnchor(float[] output, int index,
        float rawX, float rawY, float rawW, float rawH, float obj, (float c0, float c1) classProbs)
    {
        var b = index * Channels;
        output[b + 0] = rawX;
        output[b + 1] = rawY;
        output[b + 2] = rawW;
        output[b + 3] = rawH;
        output[b + 4] = obj;
        output[b + 5] = classProbs.c0;
        output[b + 6] = classProbs.c1;
    }
}
