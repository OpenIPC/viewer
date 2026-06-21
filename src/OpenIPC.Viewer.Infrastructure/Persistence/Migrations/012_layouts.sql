-- Phase 19.1 — tabbed layouts. Each layout owns its grid size and an ordered set
-- of camera tiles (LayoutTiles). Seed a single "Default" layout from the current
-- IncludedInGrid cameras so the existing single grid becomes the first tab with
-- no data loss.
CREATE TABLE IF NOT EXISTS Layouts (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name      TEXT    NOT NULL,
    GridSize  INTEGER NOT NULL DEFAULT 2,
    SortOrder INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS LayoutTiles (
    LayoutId INTEGER NOT NULL,
    CameraId TEXT    NOT NULL,
    Position INTEGER NOT NULL,
    PRIMARY KEY (LayoutId, CameraId)
);

CREATE INDEX IF NOT EXISTS IX_LayoutTiles_Layout ON LayoutTiles (LayoutId, Position);

INSERT INTO Layouts (Name, GridSize, SortOrder) VALUES ('Default', 2, 0);

INSERT INTO LayoutTiles (LayoutId, CameraId, Position)
    SELECT (SELECT Id FROM Layouts ORDER BY Id LIMIT 1),
           Id,
           ROW_NUMBER() OVER (ORDER BY SortOrder, Name COLLATE NOCASE) - 1
    FROM Cameras
    WHERE IncludedInGrid = 1;
