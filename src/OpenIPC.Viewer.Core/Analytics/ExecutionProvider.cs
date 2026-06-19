namespace OpenIPC.Viewer.Core.Analytics;

// Which ONNX Runtime execution provider actually backed the loaded session.
// The detector tries the platform-preferred provider and silently falls back
// to Cpu if it fails to initialize (Phase 15.2 — never crash because of GPU).
// Surfaced in the control center so the user sees what really runs.
public enum ExecutionProvider
{
    Cpu = 0,
    DirectMl,   // Windows
    Cuda,       // Linux/Windows NVIDIA
    OpenVino,   // Linux Intel
    CoreMl,     // macOS / iOS
    NnApi,      // Android
    Xnnpack,    // mobile CPU SIMD
}
