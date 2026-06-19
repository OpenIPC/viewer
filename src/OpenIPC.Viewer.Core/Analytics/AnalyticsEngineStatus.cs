namespace OpenIPC.Viewer.Core.Analytics;

// Lifecycle of the analytics engine for the control-center status line
// (Phase 15.7). Preparing covers the model cache-check + download (the only
// step that may need the network), Loading is the ONNX session warm-up.
public enum AnalyticsEngineStatus
{
    NotStarted = 0,
    Preparing,
    Loading,
    Ready,
    Failed,
}
