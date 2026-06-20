using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Video;

// Probes a recorded file's container metadata without decoding (Phase 16.2).
// Used to get an *accurate* duration/codec instead of guessing from the
// filename or (EndedAt - StartedAt), which drifts on crash-truncated segments.
public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string filePath, CancellationToken ct);
}

public readonly record struct MediaInfo(
    TimeSpan Duration,
    string? Codec,
    int Width,
    int Height);
