-- Phase 3.6 — per-camera checkbox controls whether the camera shows up in
-- the Live grid. Defaults to 1 so existing rows stay visible after upgrade.
ALTER TABLE Cameras ADD COLUMN IncludedInGrid INTEGER NOT NULL DEFAULT 1;
