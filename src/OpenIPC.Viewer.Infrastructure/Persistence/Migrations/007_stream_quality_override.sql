-- Phase 12.2 — per-camera SD/HD override. 0 = Auto (follow global Auto SD/HD +
-- layout), 1 = always mainstream, 2 = always substream. Existing rows default
-- to Auto, preserving current behavior.
ALTER TABLE Cameras ADD COLUMN StreamQualityOverride INTEGER NOT NULL DEFAULT 0;
