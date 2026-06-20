-- Phase 17 — audio capability flags. Set after an ONVIF probe finds an audio
-- encoder (mic → HasAudioIn) and/or an audio output config (speaker/backchannel
-- → HasAudioOut). HasAudioIn gates "listen", HasAudioOut gates "talk". Existing
-- rows stay at 0 until the next probe.
ALTER TABLE Cameras ADD COLUMN HasAudioIn INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Cameras ADD COLUMN HasAudioOut INTEGER NOT NULL DEFAULT 0;
