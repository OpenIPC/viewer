-- Phase 15 — per-camera AI object detection. Disabled by default. AiClasses is
-- a CSV of COCO class ids to keep (NULL/empty = all classes). Existing rows
-- inherit the defaults, preserving current behavior.
ALTER TABLE Cameras ADD COLUMN AiEnabled          INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Cameras ADD COLUMN AiClasses          TEXT;
ALTER TABLE Cameras ADD COLUMN AiThreshold        REAL    NOT NULL DEFAULT 0.5;
ALTER TABLE Cameras ADD COLUMN AiFps              INTEGER NOT NULL DEFAULT 3;
ALTER TABLE Cameras ADD COLUMN AiAutoRecord       INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Cameras ADD COLUMN AiPostEventSeconds INTEGER NOT NULL DEFAULT 15;
