namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Distinguishes user-captured snapshots from images owned by other features
/// (e.g. Phase 7 event thumbnails). The Browser can surface both without the
/// Snapshots table duplicating that storage.
/// </summary>
public enum SnapshotKind
{
    /// <summary>Captured by the user via the snapshot button.</summary>
    Manual = 0,

    /// <summary>An event thumbnail surfaced in the browser (not owned here).</summary>
    Event = 1,
}
