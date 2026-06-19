using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Analytics;

// Per-camera analytics configuration (Phase 15.4), resolved from persisted
// camera fields. ClassIds empty means "all classes" (the filter is a way to
// cut false positives, not a kill switch) — the editor pre-selects sensible
// classes like person/car so the empty case is rare.
public sealed record AnalyticsSettings(
    bool Enabled = false,
    IReadOnlyCollection<int>? ClassIds = null,
    float ConfidenceThreshold = 0.5f,
    int AnalyticsFps = 3,
    bool AutoRecord = false,
    int PostEventSeconds = 15)
{
    public DetectOptions ToDetectOptions() => new(
        ConfidenceThreshold,
        NmsIouThreshold: 0.45f,
        ClassFilter: ClassIds is { Count: > 0 } ? ClassIds : null);

    public static AnalyticsSettings Disabled { get; } = new();
}
