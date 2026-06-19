using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Analytics;

// ONNX Runtime object detector (Phase 15.1/15.2). Loads a YOLOX model, selects
// the platform execution provider with a mandatory CPU fallback, and runs
// synchronous inference (the sampling stage owns the worker thread). Not
// thread-safe across concurrent Detect calls — one session, one caller.
public sealed class OnnxObjectDetector : IObjectDetector
{
    private readonly ILogger<OnnxObjectDetector> _log;
    private readonly object _gate = new();

    private InferenceSession? _session;
    private ModelSpec? _spec;
    private string? _inputName;

    public OnnxObjectDetector(ILogger<OnnxObjectDetector> log) => _log = log;

    public bool IsLoaded => _session is not null;
    public ExecutionProvider ActiveProvider { get; private set; } = ExecutionProvider.Cpu;

    public async Task LoadAsync(ModelSpec model, AiAcceleration acceleration, CancellationToken ct)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));

        var (session, provider) = await Task.Run(() => CreateSession(model, acceleration), ct)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _session?.Dispose();
            _session = session;
            _spec = model;
            _inputName = FirstInputName(session);
            ActiveProvider = provider;
        }

        _log.LogInformation("Detector loaded: {Model} on {Provider} (input {Name})",
            model.Name, provider, _inputName);
    }

    private (InferenceSession, ExecutionProvider) CreateSession(ModelSpec model, AiAcceleration acceleration)
    {
        var chain = acceleration == AiAcceleration.ForceCpu
            ? Array.Empty<(string Name, ExecutionProvider Provider)>()
            : ExecutionProviderChain.ForCurrentPlatform();

        foreach (var (name, provider) in chain)
        {
            try
            {
                var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                options.AppendExecutionProvider(name);
                var session = new InferenceSession(model.FilePath, options);
                return (session, provider);
            }
            catch (Exception ex)
            {
                _log.LogInformation("Execution provider {Provider} unavailable ({Error}); trying next.",
                    provider, ex.Message);
            }
        }

        // CPU is always available — this is the guaranteed fallback path.
        var cpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return (new InferenceSession(model.FilePath, cpuOptions), ExecutionProvider.Cpu);
    }

    public IReadOnlyList<Detection> Detect(FrameBuffer frame, DetectOptions options)
    {
        if (frame is null) throw new ArgumentNullException(nameof(frame));

        InferenceSession session;
        ModelSpec spec;
        string inputName;
        lock (_gate)
        {
            if (_session is null || _spec is null || _inputName is null)
                throw new InvalidOperationException("Detector is not loaded. Call LoadAsync first.");
            session = _session;
            spec = _spec;
            inputName = _inputName;
        }

        var letterbox = new LetterboxTransform(frame.Width, frame.Height, spec.InputWidth, spec.InputHeight);
        var input = BuildInputTensor(frame, letterbox, spec.InputWidth, spec.InputHeight);

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var results = session.Run(inputs);
        var output = results[0].AsTensor<float>().ToArray();

        var raw = YoloxPostProcessor.Process(
            output, spec.ClassCount, spec.InputWidth, spec.InputHeight,
            spec.Strides, spec.GridDecodeRequired, options);

        var detections = new List<Detection>(raw.Count);
        foreach (var r in raw)
        {
            var name = r.ClassId >= 0 && r.ClassId < spec.ClassNames.Count
                ? spec.ClassNames[r.ClassId]
                : $"class{r.ClassId}";
            detections.Add(letterbox.MapToSource(r.ClassId, name, r.Confidence, r.X, r.Y, r.Width, r.Height));
        }

        return detections;
    }

    // BGRA source -> CHW float tensor in YOLOX layout: BGR channel order, raw
    // 0..255 values (no /255 — the YOLOX export folds normalization in), padded
    // with 114, aspect-preserving (nearest-neighbour resize keeps this cheap).
    private static DenseTensor<float> BuildInputTensor(
        FrameBuffer frame, LetterboxTransform lb, int inputW, int inputH)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, inputH, inputW });
        var buf = tensor.Buffer.Span;
        buf.Fill(114f);

        var plane = inputH * inputW;
        var dstX0 = (int)MathF.Round(lb.PadX);
        var dstY0 = (int)MathF.Round(lb.PadY);
        var scaledW = (int)MathF.Round(frame.Width * lb.Scale);
        var scaledH = (int)MathF.Round(frame.Height * lb.Scale);
        var invScale = 1f / lb.Scale;

        for (var dy = 0; dy < scaledH; dy++)
        {
            var outY = dstY0 + dy;
            if (outY < 0 || outY >= inputH) continue;
            var sy = Math.Min(frame.Height - 1, (int)(dy * invScale));

            for (var dx = 0; dx < scaledW; dx++)
            {
                var outX = dstX0 + dx;
                if (outX < 0 || outX >= inputW) continue;
                var sx = Math.Min(frame.Width - 1, (int)(dx * invScale));

                var so = sy * frame.Stride + sx * 4;
                var p = outY * inputW + outX;
                buf[p] = frame.Bgra[so];                 // B
                buf[plane + p] = frame.Bgra[so + 1];     // G
                buf[2 * plane + p] = frame.Bgra[so + 2]; // R
            }
        }

        return tensor;
    }

    private static string FirstInputName(InferenceSession session)
    {
        foreach (var key in session.InputMetadata.Keys)
            return key;
        throw new InvalidOperationException("Model has no inputs.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
        return default;
    }
}
