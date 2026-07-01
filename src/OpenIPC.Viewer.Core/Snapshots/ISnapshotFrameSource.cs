using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Grabs a single still frame (JPEG bytes) for a camera as cheaply as possible —
/// an HTTP snapshot endpoint, never a video decoder. Powers the grid "stills"
/// mode (periodic image refresh instead of a live RTSP session) and the private
/// timelapse archive. Returns null when no cheap source is available for the
/// camera (caller shows a placeholder / keeps the last frame).
/// </summary>
public interface ISnapshotFrameSource
{
    /// <summary>
    /// True when the camera exposes a cheap HTTP still endpoint (OpenIPC/Majestic
    /// <c>/image.jpg</c>). Cameras without one keep their live RTSP view in grid
    /// stills mode instead of blanking to an empty tile.
    /// </summary>
    bool Supports(Camera camera);

    Task<byte[]?> GrabAsync(Camera camera, CancellationToken ct);
}
