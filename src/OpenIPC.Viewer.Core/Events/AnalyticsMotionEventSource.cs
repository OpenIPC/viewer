using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Events;

// Turns AI detections into motion ticks (Phase 15.7) so detection events flow
// through the same ingestion path as motion — debounce, quiet-close, and the
// live Events observable all come for free. Each tick carries Kind=Detection
// and a class-count summary like "person ×2, car ×1".
public sealed class AnalyticsMotionEventSource : IMotionEventSource
{
    private readonly IAnalyticsEngine _engine;

    public AnalyticsMotionEventSource(IAnalyticsEngine engine) => _engine = engine;

    public string Name => "analytics";

    public IDisposable Watch(CameraId cameraId, IObserver<MotionTick> observer, CancellationToken ct)
        => _engine.Results.Subscribe(new ResultObserver(cameraId, observer));

    // Builds "person ×2, car ×1" ordered by descending count.
    public static string Summarize(IReadOnlyList<Detection> detections)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in detections)
            counts[d.ClassName] = counts.TryGetValue(d.ClassName, out var n) ? n + 1 : 1;

        var parts = new List<KeyValuePair<string, int>>(counts);
        parts.Sort((a, b) => b.Value.CompareTo(a.Value));

        var sb = new StringBuilder();
        foreach (var kv in parts)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(kv.Key).Append(" ×").Append(kv.Value);
        }
        return sb.ToString();
    }

    private sealed class ResultObserver : IObserver<DetectionResult>
    {
        private readonly CameraId _cameraId;
        private readonly IObserver<MotionTick> _target;

        public ResultObserver(CameraId cameraId, IObserver<MotionTick> target)
        {
            _cameraId = cameraId;
            _target = target;
        }

        public void OnNext(DetectionResult value)
        {
            if (value.CameraId != _cameraId || value.Detections.Count == 0) return;
            _target.OnNext(new MotionTick(
                _cameraId, value.OccurredAt, "analytics",
                EventKind.Detection, Summarize(value.Detections)));
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
