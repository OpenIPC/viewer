using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Captures a still and indexes it. Always prefers an HD source: an
/// already-running mainstream session, else the Majestic HTTP snapshot, else a
/// briefly-opened mainstream — only falling back to a running substream when
/// none of those are available. See <see cref="SnapshotService"/>.
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// Captures, saves, thumbnails and DB-indexes a snapshot for
    /// <paramref name="camera"/>. <paramref name="liveSession"/> /
    /// <paramref name="liveQuality"/> describe the caller's currently-open
    /// session (if any) so a live mainstream frame can be grabbed for free.
    /// </summary>
    Task<Snapshot> CaptureAsync(
        Camera camera,
        IVideoSession? liveSession,
        StreamQuality? liveQuality,
        CancellationToken ct);
}
