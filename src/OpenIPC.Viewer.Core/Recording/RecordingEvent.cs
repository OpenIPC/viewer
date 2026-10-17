using System;

namespace OpenIPC.Viewer.Core.Recording;

public abstract record RecordingEvent
{
    public sealed record Started(string FirstSegmentPath, DateTime Time) : RecordingEvent;
    public sealed record SegmentRotated(string PrevPath, string NewPath, DateTime Time, long? Size) : RecordingEvent;
    // Detail: the recorder's own reason for a stop it wasn't asked for.
    public sealed record Stopped(DateTime Time, RecordingStopReason Reason, string? Detail = null) : RecordingEvent;
    public sealed record Error(string Message) : RecordingEvent;
}

public enum RecordingStopReason
{
    User = 0,
    ProcessExited,
    DiskFull,
    Error,
}
