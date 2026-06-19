using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Analytics;

// Describes an ONNX detection model: where the file is, its input geometry, the
// class label list, and how to decode its output. Kept model-agnostic on
// purpose (Phase 15 technical notes — the post-processor is pluggable); the
// YOLOX factories below pin the values for the bundled Apache-2.0 models.
public sealed record ModelSpec(
    string Name,
    string FilePath,
    int InputWidth,
    int InputHeight,
    IReadOnlyList<string> ClassNames,
    IReadOnlyList<int> Strides,
    bool GridDecodeRequired)
{
    public int ClassCount => ClassNames.Count;

    // YOLOX-tiny / YOLOX-nano: 416×416 input, FPN strides 8/16/32, the official
    // ONNX export leaves box coords undecoded (grid + exp(·)·stride applied at
    // post-process time), obj/cls are already sigmoid-activated.
    public static ModelSpec YoloxTiny(string filePath) =>
        new("YOLOX-tiny", filePath, 416, 416, CocoClasses.Names, new[] { 8, 16, 32 }, GridDecodeRequired: true);

    public static ModelSpec YoloxNano(string filePath) =>
        new("YOLOX-nano", filePath, 416, 416, CocoClasses.Names, new[] { 8, 16, 32 }, GridDecodeRequired: true);
}
