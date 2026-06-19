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
    // Official YOLOX-tiny ONNX export from the upstream release assets.
    // TODO: pin Sha256Hex once the asset is downloaded and verified in CI;
    // until then integrity checking is skipped (logged).
    public static ModelDescriptor YoloxTiny { get; } = new(
        Name: "YOLOX-tiny",
        FileName: "yolox_tiny.onnx",
        DownloadUri: new Uri("https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_tiny.onnx"),
        Sha256Hex: null,
        CreateSpec: ModelSpec.YoloxTiny);

    public static ModelDescriptor Default => YoloxTiny;
}
