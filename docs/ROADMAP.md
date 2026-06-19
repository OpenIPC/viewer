# Roadmap

High-level status of OpenIPC Viewer. The project is built in phases — each
phase ends with a demonstrable, releasable increment. This page tracks where
things stand; it is a summary, not a commitment to dates.

> **Current status:** feature-complete for the first desktop beta. Work in
> progress is **Phase 11** — release polish (packaging, auto-update, signing).

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
| 11 | Onboarding + polish + release | Packaging, auto-update, signing, store delivery | 🚧 In progress |

## Milestones

- **`v0.1.0-beta` (desktop)** — Phases 0–8 plus release polish.
  Windows / Linux / macOS standalone builds.
- **`v0.2.0-beta` (mobile)** — Phases 9–10. Android + iOS heads.

## Phase 11 — remaining

Polish items still open before tagging the betas:

- [ ] QR-code camera add (scan to provision)
- [ ] In-app auto-update (Velopack) + native installers
- [ ] Code signing (Windows / macOS) and Play / TestFlight signing
- [ ] F-Droid packaging
- [x] Snapshot share sheet
- [ ] Localization polish (English / Russian)

> **Validation caveat.** Linux / macOS / Android / iOS code paths build and
> link in CI but are not yet end-to-end tested on real devices for every
> commit. Feedback is welcome — open an issue with OS version and steps.

## Post-MVP — planned enhancements (Phases 12+)

Enhancement phases distilled from a full review of competing-client release
notes, all designed cross-platform (Win / Lin / Mac / Android / iOS). Rationale
and scope live in the planning docs (`dashboard-ideas-roadmap-ru.md`).

| # | Phase | Scope | Status |
|---|---|---|:---:|
| 12 | Streaming hardening | Smart-pause hidden tiles, auto SD/HD, watchdog + backoff, last-frame hold, error tile | ✅ Done |
| 13 | SSH device suite | SSH terminal, SCP file manager, open-in-browser, config push | ✅ Done |
| 14 | Snapshots & viewer | Always-HD snapshot, snapshot browser, built-in viewer + basic editor | ✅ Done |
| 15 | Local AI analytics | ONNX object detection per camera, auto-record, control center, CPU fallback | ✅ Done |
| 16 | Archive pro | Fragmented MP4, activity calendar, timeline zoom, clip export | 📋 Planned |
| 17 | Community & app-level | Tabbed layouts, config export/import, notifications, white-label, issue reporter, RBAC | 📋 Planned |
| 18 | Streq remote access | Cloud multistreaming across devices: LAN/overlay/relay routing, enrollment, WebRTC/HLS, cross-device sync | 📋 Planned |

> **Phase 18** is the viewer side of our own **Streq** cloud (WireGuard/n3n
> overlay + go2rtc/MediaMTX media relay) for remote multistreaming across
> devices. The cloud/agent side lives in a separate `streq` repo with its own
> phasing; Phase 18 starts once the Streq coordinator (Stage I) is up, so it can
> run as a parallel track to Phases 12–17.
