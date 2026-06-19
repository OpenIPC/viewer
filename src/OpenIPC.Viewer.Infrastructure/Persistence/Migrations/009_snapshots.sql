-- Phase 14 — snapshot index. One row per captured still.
-- TakenAt stores UTC ISO-8601; the UI converts to local for the timeline.
-- Width/Height are the full-image pixel dimensions, so the browser can prove
-- an HD-always capture. Source/Kind are the SnapshotSource/SnapshotKind enums.
-- ThumbPath may be NULL until the gallery thumbnail has been generated.
CREATE TABLE Snapshots (
    Id        TEXT NOT NULL PRIMARY KEY,
    CameraId  TEXT NOT NULL REFERENCES Cameras(Id) ON DELETE CASCADE,
    TakenAt   TEXT NOT NULL,
    Path      TEXT NOT NULL,
    ThumbPath TEXT,
    Width     INTEGER NOT NULL,
    Height    INTEGER NOT NULL,
    Source    INTEGER NOT NULL,
    Kind      INTEGER NOT NULL
);
CREATE INDEX Idx_Snapshots_Camera_Taken ON Snapshots(CameraId, TakenAt DESC);
CREATE INDEX Idx_Snapshots_Taken ON Snapshots(TakenAt DESC);
