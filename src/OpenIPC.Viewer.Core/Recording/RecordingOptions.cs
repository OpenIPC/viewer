using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Recording;

public sealed record RecordingOptions(
    CameraId CameraId,
    Uri RtspUri,
    CameraCredentials? Credentials,
    string OutputDirectory,
    string FilenamePattern,    // strftime, e.g. "cam_%Y%m%d_%H%M%S.mp4"
    TimeSpan SegmentDuration);
