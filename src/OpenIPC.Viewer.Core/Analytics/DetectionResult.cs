using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Analytics;

// One inference outcome for a camera (Phase 15.3). Carries the detections (for
// the overlay + counter), when it happened, and how long inference took (for
// the control center latency readout).
public sealed record DetectionResult(
    CameraId CameraId,
    DateTime OccurredAt,
    IReadOnlyList<Detection> Detections,
    double InferenceMs);
