-- Phase 13 — SSH device suite. Optional per-camera SSH port; NULL means the
-- default 22. SSH credentials are not stored here — they live in the secrets
-- store under cam:{id}:ssh:username / cam:{id}:ssh:password.
ALTER TABLE Cameras ADD COLUMN SshPort INTEGER NULL;
