namespace OpenIPC.Viewer.Core.Analytics;

// Snapshot of engine health for the control center Diagnostics section
// (Phase 15.7). FramesDropped surfaces the bounded-queue back-pressure that was
// the dashboard's memory-leak trap — a steady non-zero drop count is expected
// under load; a growing queue depth is the warning sign.
public sealed record AnalyticsDiagnostics(
    int ActiveCameras,
    long FramesSampled,
    long FramesProcessed,
    long FramesDropped,
    int QueueDepth,
    double AverageLatencyMs)
{
    public static AnalyticsDiagnostics Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
