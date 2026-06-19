using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Analytics;

// A single decoded candidate in INPUT-pixel space (top-left x/y + w/h), before
// letterbox un-mapping. Kept separate from Detection so the post-processor
// stays pure and testable without any frame/letterbox context.
public readonly record struct RawDetection(
    int ClassId,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height);

// Pure YOLOX output decoder + non-max suppression (Phase 15.1). The official
// YOLOX ONNX export emits, per anchor, [cx, cy, w, h, obj, cls0..clsN] where
// obj/cls are already sigmoid-activated and the box needs grid+stride decode
// (cx,cy = (raw+grid)*stride, w,h = exp(raw)*stride). No ONNX Runtime types
// here on purpose — feed it a flat float[] and it returns boxes, so unit tests
// can drive synthetic tensors.
public static class YoloxPostProcessor
{
    // Total anchors a square-strided model emits for the given input geometry.
    public static int AnchorCount(int inputWidth, int inputHeight, IReadOnlyList<int> strides)
    {
        var total = 0;
        foreach (var s in strides)
            total += (inputWidth / s) * (inputHeight / s);
        return total;
    }

    public static IReadOnlyList<RawDetection> Process(
        float[] output,
        int classCount,
        int inputWidth,
        int inputHeight,
        IReadOnlyList<int> strides,
        bool gridDecode,
        DetectOptions options)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (strides is null) throw new ArgumentNullException(nameof(strides));

        var channels = 5 + classCount;
        var anchors = AnchorCount(inputWidth, inputHeight, strides);
        if (output.Length != anchors * channels)
        {
            throw new ArgumentException(
                $"Output length {output.Length} does not match {anchors} anchors × {channels} channels.",
                nameof(output));
        }

        var threshold = options.ConfidenceThreshold;
        var filter = options.ClassFilter;
        var candidates = new List<RawDetection>();

        var anchor = 0;
        foreach (var stride in strides)
        {
            var gridW = inputWidth / stride;
            var gridH = inputHeight / stride;
            for (var gy = 0; gy < gridH; gy++)
            {
                for (var gx = 0; gx < gridW; gx++, anchor++)
                {
                    var b = anchor * channels;

                    float cx, cy, w, h;
                    if (gridDecode)
                    {
                        cx = (output[b + 0] + gx) * stride;
                        cy = (output[b + 1] + gy) * stride;
                        w = (float)Math.Exp(output[b + 2]) * stride;
                        h = (float)Math.Exp(output[b + 3]) * stride;
                    }
                    else
                    {
                        cx = output[b + 0];
                        cy = output[b + 1];
                        w = output[b + 2];
                        h = output[b + 3];
                    }

                    var obj = output[b + 4];
                    var bestClass = -1;
                    var bestProb = 0f;
                    for (var c = 0; c < classCount; c++)
                    {
                        var p = output[b + 5 + c];
                        if (p > bestProb)
                        {
                            bestProb = p;
                            bestClass = c;
                        }
                    }

                    var score = obj * bestProb;
                    if (bestClass < 0 || score < threshold) continue;
                    if (filter is not null && !filter.Contains(bestClass)) continue;

                    candidates.Add(new RawDetection(bestClass, score, cx - w / 2f, cy - h / 2f, w, h));
                }
            }
        }

        return NonMaxSuppression(candidates, options.NmsIouThreshold);
    }

    // Greedy per-class NMS: keep the highest-scoring box, drop same-class boxes
    // that overlap it beyond the IoU threshold.
    public static IReadOnlyList<RawDetection> NonMaxSuppression(
        List<RawDetection> candidates, float iouThreshold)
    {
        candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var kept = new List<RawDetection>();
        var removed = new bool[candidates.Count];

        for (var i = 0; i < candidates.Count; i++)
        {
            if (removed[i]) continue;
            var a = candidates[i];
            kept.Add(a);
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (removed[j] || candidates[j].ClassId != a.ClassId) continue;
                if (Iou(a, candidates[j]) > iouThreshold) removed[j] = true;
            }
        }

        return kept;
    }

    private static float Iou(in RawDetection a, in RawDetection b)
    {
        var ax2 = a.X + a.Width;
        var ay2 = a.Y + a.Height;
        var bx2 = b.X + b.Width;
        var by2 = b.Y + b.Height;

        var ix1 = Math.Max(a.X, b.X);
        var iy1 = Math.Max(a.Y, b.Y);
        var ix2 = Math.Min(ax2, bx2);
        var iy2 = Math.Min(ay2, by2);

        var iw = Math.Max(0f, ix2 - ix1);
        var ih = Math.Max(0f, iy2 - iy1);
        var inter = iw * ih;
        if (inter <= 0f) return 0f;

        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0f ? 0f : inter / union;
    }
}
