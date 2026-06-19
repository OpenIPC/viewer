using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Analytics;

// Per-detect tuning (Phase 15.4 — sourced from per-camera settings). ClassFilter
// null means "keep all classes"; an empty set means "keep none" (analytics
// effectively idle). Thresholds cut false positives from the small models.
public sealed record DetectOptions(
    float ConfidenceThreshold = 0.5f,
    float NmsIouThreshold = 0.45f,
    IReadOnlyCollection<int>? ClassFilter = null);
