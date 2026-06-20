using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Archive;

// Clip export request (Phase 16.5). Start/End are offsets from the start of the
// source file. Precise = re-encode for frame-accurate cuts; otherwise a fast
// stream-copy snapped to GOP/keyframe boundaries.
public sealed record ClipExportRequest(
    string SourcePath,
    string DestinationPath,
    TimeSpan Start,
    TimeSpan End,
    bool Precise);

public interface IClipExporter
{
    // Reports 0..1 progress; throws on failure; respects cancellation.
    Task ExportAsync(ClipExportRequest request, IProgress<double>? progress, CancellationToken ct);
}

// Pure boundary math for stream-copy export (Phase 16.5/16.7). A `-c copy` cut
// can only start on a keyframe, so the real clip begins at the last keyframe at
// or before the requested in-point (GOP accuracy). Frame-accurate cuts need a
// re-encode (the "precise" path).
public static class ClipBounds
{
    public static TimeSpan SnapStartToKeyframe(TimeSpan requestedStart, IReadOnlyList<TimeSpan> keyframes)
    {
        var best = TimeSpan.Zero;
        foreach (var kf in keyframes)
            if (kf <= requestedStart && kf > best)
                best = kf;
        return best;
    }

    public static TimeSpan Duration(TimeSpan start, TimeSpan end) =>
        end > start ? end - start : TimeSpan.Zero;
}
