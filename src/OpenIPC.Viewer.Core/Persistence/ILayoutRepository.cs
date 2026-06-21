using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Persistence;

// Named Live-grid layouts + their tile membership (Phase 19.1). Membership lives
// in a join table (LayoutTiles) keyed by position, so each layout owns its own
// ordered set of cameras and grid size — independent of the global IncludedInGrid
// flag, which the migration seeds the first layout from.
public interface ILayoutRepository
{
    Task<IReadOnlyList<GridLayout>> GetAllAsync(CancellationToken ct);
    Task<LayoutId> AddAsync(string name, int gridSize, int sortOrder, CancellationToken ct);
    Task RenameAsync(LayoutId id, string name, CancellationToken ct);
    Task SetGridSizeAsync(LayoutId id, int gridSize, CancellationToken ct);
    Task RemoveAsync(LayoutId id, CancellationToken ct);
    // Persist tab order: SortOrder = index in the list.
    Task ReorderAsync(IReadOnlyList<LayoutId> orderedIds, CancellationToken ct);

    // Tile membership, ordered by position. GetTiles excludes cameras that no
    // longer exist (join to Cameras), so deleting a camera can't leave ghosts.
    Task<IReadOnlyList<CameraId>> GetTilesAsync(LayoutId id, CancellationToken ct);
    Task SetTilesAsync(LayoutId id, IReadOnlyList<CameraId> cameraIds, CancellationToken ct);
    Task AddTileAsync(LayoutId id, CameraId cameraId, CancellationToken ct);
    Task RemoveTileAsync(LayoutId id, CameraId cameraId, CancellationToken ct);
}
