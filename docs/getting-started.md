# Getting started

The [README](../README.md) has per-platform install instructions for all five
targets — Windows / Linux / macOS / Android / iOS. This file expands on a few
points that don't fit there.

## Where things land

| Thing | Location |
|---|---|
| App settings UI writes | `usersettings.json` in your platform's AppData root |
| Camera DB | `openipc-viewer.db` (SQLite) in the same root |
| Recordings | `recordings/` (or `~/Movies/OpenIPC` on Linux/macOS if `XDG_VIDEOS_DIR` is set) |
| Snapshots | `snapshots/` (or `~/Pictures/OpenIPC` on Linux/macOS) |
| Logs | `logs/openipc-viewer-{date}.log` (rolling daily, 7-day retention) |

Per-platform AppData root paths are in the README's **User data** section.

## First-run flow

On first launch the library is empty and a welcome dialog appears — it also
carries a language picker (English / Russian, switchable later in Settings) —
with four choices:

1. **Scan local network** — aggregated discovery: ONVIF WS-Discovery and mDNS
   run together, with an optional *Deep scan* subnet sweep for cameras that
   announce nothing. Most cameras with a working ONVIF responder are picked up
   within ~5 s; each result has its own *Add*. See
   [Adding your first camera](first-camera.md).
2. **Scan QR code** — pick a saved QR image (screenshot, photo, sticker) and
   the camera editor opens pre-filled from it.
3. **Add manually** — type the address and login and press *Connect*; the
   editor detects the camera type and fills in the streams. Use this if
   discovery misses the camera or if it's on a different VLAN.
4. **Skip** — opens an empty library; add cameras later from *Add* or *Find
   on network* in the Library header.

The "show this once" flag is persisted; dismissing it once means the dialog
doesn't reappear even if you later delete every camera.

## Tuning

- **Settings → Appearance → Start page** — open on the Library (default) or
  straight on Live. When starting on Live, *Layout at startup* picks the
  layout it opens on; *Last used* keeps whatever was open when you quit.
- **Settings → Video → Show telemetry overlay** — toggles the live-view
  badges (codec / fps / frames). Off by default for a cleaner picture during
  recordings.
- **Settings → Video → Default RTSP transport** — `tcp` is the safer default
  on lossy networks. Switch to `udp` only if you've ruled out packet loss.
- **Settings → Advanced → Verbose logging** — flips Serilog's minimum level
  to Debug live, no restart needed. Useful when something doesn't connect
  and you want to see why before pinging an issue.

## Moving to another machine

*Settings → Backup & restore → Export…* writes cameras and layouts to a single
JSON; *Import…* reads it back on the other machine, showing a preview of how
many cameras and layouts will be added or updated before it commits. Layouts
are matched by name, so importing the same file twice doesn't duplicate tabs.
Passwords stay out of the file unless you tick *Sync camera passwords
(encrypted)* and set a shared passphrase in the *Network config auto-update*
section — then they travel encrypted with it.

That same section can keep several machines in step: point it at a shared
JSON (a local or network path) and every start mirrors cameras and layouts from
it. The file wins — cameras and layouts missing from it are removed locally —
and if the path is unreachable the last working config keeps running.

## Running it as a server

The same binary can serve a browser UI to the rest of your network instead of
opening a window (`--server-only`). Flags, accounts and reverse-proxy examples
are in the self-hosting guide ([English](web-server.md) ·
[Русский](web-server.ru.md)).
