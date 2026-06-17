using System;

namespace OpenIPC.Viewer.Core.Video;

public sealed record SessionTelemetry(
    int FramesDecoded,
    int FramesDropped,
    double Fps,
    TimeSpan AverageLatency,
    string? Codec,
    int Width,
    int Height,
    DateTime CapturedAt,
    double BitrateKbps = 0);
