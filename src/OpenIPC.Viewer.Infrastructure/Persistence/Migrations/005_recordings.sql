-- Phase 6 — recording segments. One row per segment file. EndedAt null while
-- the segment is being written; finalized on rotation or stop. SizeBytes is
-- best-effort (filled at finalize) — UI shows '?' for null.
CREATE TABLE Recordings (
    Id          TEXT NOT NULL PRIMARY KEY,
    CameraId    TEXT NOT NULL REFERENCES Cameras(Id) ON DELETE CASCADE,
    FilePath    TEXT NOT NULL UNIQUE,
    StartedAt   TEXT NOT NULL,
    EndedAt     TEXT,
    SizeBytes   INTEGER NOT NULL DEFAULT 0,
    Codec       TEXT,
    HasMotion   INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX Idx_Recordings_CameraId_StartedAt ON Recordings(CameraId, StartedAt DESC);
