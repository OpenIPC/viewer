using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// One indexed snapshot. <see cref="TakenAt"/> is UTC (the UI converts to
/// local). <see cref="Path"/> points at the full-resolution JPEG;
/// <see cref="ThumbPath"/> at the cached gallery thumbnail (null if not yet
/// generated). <see cref="Width"/>/<see cref="Height"/> are the full-image
/// pixel dimensions, used to prove HD capture.
/// </summary>
public sealed record Snapshot(
    SnapshotId Id,
    CameraId CameraId,
    DateTime TakenAt,
    string Path,
    string? ThumbPath,
    int Width,
    int Height,
    SnapshotSource Source,
    SnapshotKind Kind);
