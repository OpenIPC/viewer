namespace OpenIPC.Viewer.Core.Entities;

// A named Live-grid layout (Phase 19.1) — its own NxN grid size plus an ordered
// set of camera tiles (stored in LayoutTiles). Users switch between layouts as
// tabs ("Home" / "Office" / "Warehouse").
public sealed record GridLayout(
    LayoutId Id,
    string Name,
    int GridSize,
    int SortOrder);
