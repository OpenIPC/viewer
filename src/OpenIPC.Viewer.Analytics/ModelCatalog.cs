using System;
using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Analytics;

// A downloadable detection model: where to fetch it, its integrity hash, and
// how to build the ModelSpec once it is on disk.
public sealed record ModelDescriptor(
    string Name,
    string FileName,
    Uri? DownloadUri,
    string? Sha256Hex,
    Func<string, ModelSpec> CreateSpec);

// Known models. We ship Apache-2.0 YOLOX (never AGPL Ultralytics YOLO) to stay
// MIT/store compatible. The model is an asset fetched on first enable, not
// checked into the repo.
public static class ModelCatalog
{
    // Official YOLOX-tiny ONNX export from the upstream release assets
    // (~20 MB). SHA-256 verified against the 0.1.1rc0 asset on 2026-06-19.
    public static ModelDescriptor YoloxTiny { get; } = new(
        Name: "YOLOX-tiny",
        FileName: "yolox_tiny.onnx",
        DownloadUri: new Uri("https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_tiny.onnx"),
        Sha256Hex: "427CC366D34E27FF7A03E2899B5E3671425C262EA2291F88BB942BC1CC70B0F7",
        CreateSpec: ModelSpec.YoloxTiny);

    public static ModelDescriptor Default => YoloxTiny;
}
