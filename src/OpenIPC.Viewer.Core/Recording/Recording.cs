using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Recording;

public sealed record Recording(
    RecordingId Id,
    CameraId CameraId,
    string FilePath,
    DateTime StartedAt,
    DateTime? EndedAt,
    long SizeBytes,
    string? Codec,
    bool HasMotion);
