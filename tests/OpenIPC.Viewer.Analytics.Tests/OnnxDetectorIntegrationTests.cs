using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Analytics;
using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Analytics.Tests;

// Real ONNX Runtime integration (Phase 15.8). Gated on OPENIPC_DETECTION_MODEL
// (a local yolox_tiny.onnx) so CI without the model — the repo ships no
// binaries — skips rather than fails. Verifies the model loads on the CPU
// provider and inference runs end to end without throwing.
public sealed class OnnxDetectorIntegrationTests
{
    [SkippableFact]
    public async Task LoadsModel_AndRunsInferenceOnCpu()
    {
        var modelPath = Environment.GetEnvironmentVariable("OPENIPC_DETECTION_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath),
            "Set OPENIPC_DETECTION_MODEL to a YOLOX ONNX file to run this test.");

        await using var detector = new OnnxObjectDetector(NullLogger<OnnxObjectDetector>.Instance);
        await detector.LoadAsync(ModelSpec.YoloxTiny(modelPath!), AiAcceleration.ForceCpu, CancellationToken.None);

        Assert.True(detector.IsLoaded);
        Assert.Equal(ExecutionProvider.Cpu, detector.ActiveProvider);

        // A black frame: inference must complete cleanly (likely no detections).
        var frame = new FrameBuffer(new byte[640 * 480 * 4], 640, 480, 640 * 4);
        var detections = detector.Detect(frame, new DetectOptions());
        Assert.NotNull(detections);
    }
}
