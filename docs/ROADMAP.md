# Roadmap

High-level status of OpenIPC Viewer. The project is built in phases — each
phase ends with a demonstrable, releasable increment. This page tracks where
things stand; it is a summary, not a commitment to dates.

> **Current status:** public beta, shipping regularly — latest release
> **`v0.3.10`** (September 2026) for Windows / Linux / macOS / Android. Every
> feature phase through 21 is done; what remains from the plan is
> **distribution** (native installers, in-app auto-update, code signing,
> F-Droid) plus on-device validation across the non-Windows heads.

## Phases

| # | Phase | Scope | Status |
|---|---|---|:---:|
| 0 | Foundation | Avalonia shell, DI, navigation, logging, CI | ✅ Done |
| 1 | Persistence | Camera CRUD, SQLite, encrypted credentials | ✅ Done |
| 2 | Video pipeline | FFmpeg decode, RTSP, HW accel, auto-reconnect | ✅ Done |
| 3 | Multi-camera grid | Up to 25 streams, custom layouts, drag-reorder | ✅ Done |
| 4 | ONVIF + PTZ | WS-Discovery probe, PTZ joystick + presets | ✅ Done |
| 5 | Majestic config | Read/apply config, diff preview, JSON editor, RTMP | ✅ Done |
| 6 | Recording | Segmented MP4 (`-c copy`), foreground service | ✅ Done |
| 7 | Events + Timeline | Motion ingestion, persisted event log | ✅ Done |
| 8 | Linux + macOS heads | VAAPI / VideoToolbox, keyring credentials | ✅ Done |
| 9 | Android head | MediaCodec decode, in-process recording | ✅ Done |
| 10 | iOS head | VideoToolbox, foreground-only recording | ✅ Done |
| 11 | Onboarding + polish + release | Packaging, auto-update, signing, store delivery | 🚧 Distribution only |

## Milestones

- **`v0.1.x-beta` (desktop)** — Phases 0–8 plus release polish.
  Windows / Linux / macOS standalone builds.
- **`v0.2.x-beta` (mobile)** — Phases 9–10. Android + iOS heads.
- **`v0.3.x` (current)** — post-MVP phases 12–17 and 19–21: AI detection,
  two-way audio, archive pro, tabbed layouts, the self-hosted web console,
  aggregated discovery, and a long tail of platform fixes. `v0.3.9`–`v0.3.10`
  added PTZ stepping, digital zoom and a redesign of the main pages.

## Phase 11 — remaining

Everything left before the betas stop being betas is packaging and signing:

- [ ] In-app auto-update (Velopack) + native installers
- [ ] Code signing (Windows / macOS) and Play / TestFlight signing
- [ ] F-Droid packaging
- [x] QR-code camera add (decode a saved QR image → pre-filled editor;
      in-camera scanning on mobile is still open)
- [x] Snapshot share sheet
- [x] Localization (English / Russian, switched at runtime)

> **Validation caveat.** Linux / macOS / Android / iOS code paths build and
> link in CI but are not yet end-to-end tested on real devices for every
> commit. Feedback is welcome — open an issue with OS version and steps.

## Post-MVP — enhancement phases (12+)

Enhancement phases distilled from a full review of competing-client release
notes, all designed cross-platform (Win / Lin / Mac / Android / iOS), plus a
server track that grew out of them.

| # | Phase | Scope | Status |
|---|---|---|:---:|
| 12 | Streaming hardening | Smart-pause hidden tiles, auto SD/HD, watchdog + backoff, last-frame hold, error tile | ✅ Done |
| 13 | SSH device suite | SSH terminal, SCP file manager, open-in-browser, config push | ✅ Done |
| 14 | Snapshots & viewer | Always-HD snapshot, snapshot browser, built-in viewer + basic editor | ✅ Done |
| 15 | Local AI analytics | ONNX object detection per camera, auto-record, control center, CPU fallback | ✅ Done |
| 16 | Archive pro | Fragmented MP4, activity calendar, timeline zoom, clip export | ✅ Done |
| 17 | Two-way audio | Listen (AAC / G.711) in grid + single view, push-to-talk over the ONVIF backchannel | ✅ Done |
| 18 | Streq remote access | Cloud multistreaming across devices: LAN/overlay/relay routing, enrollment, WebRTC/HLS, cross-device sync | 📋 Planned |
| 19 | Community & app-level | Tabbed layouts, config export/import, notifications, issue reporter | ✅ Done |
| 20 | Web server (LAN self-host) | Kestrel host behind `--server-only`, accounts + permissions, camera subsets | ✅ Done |
| 21 | Web UI as a React SPA | Live grid, PTZ, discovery, archive, snapshots, audio, Majestic panel in the browser | ✅ Done |

Deferred out of Phase 19: white-label `branding.json` and granular RBAC on the
desktop app (the web console has its own per-user permissions and camera
subsets instead).

### Shipped alongside the phases

Work that came from real-camera use rather than the phase plan, all released
in the 0.3.x line:

- **Discovery v2** — ONVIF WS-Discovery + mDNS + opt-in subnet sweep merged
  into one result list, with OpenIPC web fingerprinting, multi-add from a
  single scan, and manual IP ranges.
- **Health & status** — one status verdict per camera (online / attention /
  offline) shared by grid, library and a Health Center overview.
- **Majestic Control Center** — schema-driven `config.json` editor with live
  ISP tuning and read-only metrics, on top of the curated panel.
- **Device tools** — reboot / clock / log snapshots over SSH, plus
  user-defined SSH camera actions surfaced as grid buttons.
- **Desktop niceties** — tray icon with quick actions, close-to-tray,
  fullscreen kiosk mode, grid pagination, low-cost "stills" mode, optional
  single-instance mode.
- **Mobile UX** — safe-area insets, soft-keyboard handling for the SSH
  terminal, mobile splash, and a gate on risky device tools.
- **PTZ stepping** (`v0.3.9`) — a step keypad, home and move speed next to
  the joystick, offered only where the camera reports it can do them; ONVIF
  client hardened against firmwares that advertise wrong service addresses,
  answer only SOAP 1.1 or use digest auth.
- **Digital zoom** (`v0.3.10`) — up to 8× in live view and the recording
  player: zoom at the cursor, pan, zoom into a drawn box, minimap.
- **UI redesign** (`v0.3.10`) — add-camera dialog that connects and
  auto-detects first; find-cameras dialog with an instant quick scan; dense
  library list with search and status filters; recordings grouped by day with
  a clearer calendar; events with filters, episodes and jump-to-recording; a
  rebuilt recording player (speed, frame step, sound, fullscreen, clip mode);
  a new AI page; a regrouped live toolbar; phone-width polish throughout.
- **Start page** (`v0.3.9`) — open in Library or Live, on a chosen layout.

> **Phase 18** is the viewer side of our own **Streq** cloud (WireGuard/n3n
> overlay + go2rtc/MediaMTX media relay) for remote multistreaming across
> devices. The cloud/agent side lives in a separate `streq` repo with its own
> phasing; the viewer work starts once the Streq coordinator is up, so it runs
> as a parallel track.
