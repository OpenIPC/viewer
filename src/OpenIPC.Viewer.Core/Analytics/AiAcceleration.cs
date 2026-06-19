namespace OpenIPC.Viewer.Core.Analytics;

// Global "AI acceleration" preference (Phase 15.2). Auto lets the detector pick
// the platform-preferred execution provider with a CPU fallback; ForceCpu pins
// the CPU provider for reproducibility / troubleshooting flaky GPU stacks.
public enum AiAcceleration
{
    Auto = 0,
    ForceCpu = 1,
}
