namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Where the snapshot pixels came from. Lets the UI/diagnostics reason about
/// quality — an HD-always snapshot should never report <see cref="LiveSub"/>.
/// </summary>
public enum SnapshotSource
{
    /// <summary>Grabbed from an already-running mainstream session (HD).</summary>
    LiveMain = 0,

    /// <summary>Majestic HTTP <c>/image.jpg</c> — always full-resolution.</summary>
    HttpSnapshot = 1,

    /// <summary>Mainstream was opened briefly just to grab a keyframe (HD).</summary>
    OpenedStream = 2,

    /// <summary>Grabbed from a running substream session (SD) — fallback only.</summary>
    LiveSub = 3,

    /// <summary>Produced by the in-app editor (a saved copy).</summary>
    Edited = 4,
}
