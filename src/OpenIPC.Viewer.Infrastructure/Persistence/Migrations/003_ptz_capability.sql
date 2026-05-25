-- Phase 4c — PTZ capability flag. Set to 1 after ONVIF probe detects PTZ
-- service + a profile with PTZConfiguration. UI shows the virtual joystick
-- only when HasPtz=1. Existing rows stay at 0 until next probe.
ALTER TABLE Cameras ADD COLUMN HasPtz INTEGER NOT NULL DEFAULT 0;
