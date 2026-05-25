# OpenIPC Viewer

Cross-platform desktop and mobile viewer for OpenIPC IP cameras.
Built with .NET 9 and Avalonia 12.

> Status: **Phase 8** — Linux/macOS desktop heads with platform-native HW decode + credential storage; first public `v0.1.0-beta` release pending.

## Build and run

Requires .NET 9 SDK + PowerShell (for the FFmpeg fetch script) + Docker (only for integration tests).

```bash
# one-time: pull FFmpeg shared-build DLLs into runtimes/win-x64/native/
powershell -ExecutionPolicy Bypass -File tools/fetch-ffmpeg.ps1

dotnet restore OpenIPC.Viewer.slnx
dotnet build   OpenIPC.Viewer.slnx
dotnet test    OpenIPC.Viewer.slnx --no-build
dotnet run --project src/OpenIPC.Viewer.Desktop
```

Build runs with `TreatWarningsAsErrors=true`; any warning fails the build.

## Native dependencies

**Windows:** FFmpeg shared-build DLLs (LGPL, version pin `n7.1`) are **not**
committed. `tools/fetch-ffmpeg.ps1` downloads them from `BtbN/FFmpeg-Builds`
releases into `runtimes/win-x64/native/`, which the Video project copies into
the Desktop output. Re-run the script when bumping the FFmpeg pin.

**Linux:** install FFmpeg + libsecret from the distro repos:
```
sudo apt install ffmpeg libavcodec-extra libsecret-1-0 libsecret-tools
```
The app uses the system FFmpeg via the standard loader path. `secret-tool` is
used to read/write credentials in the GNOME/KDE keyring; if D-Bus isn't
available (headless servers) an AES-GCM file fallback kicks in automatically.
VAAPI hardware decode needs `/dev/dri/renderD128` and your user in the
`render` (or `video`) group.

**macOS:** install FFmpeg via Homebrew:
```
brew install ffmpeg
```
Credentials live in the macOS Keychain via the built-in `security` tool.
VideoToolbox HW decode works on any Mac 12+ (.NET 9's minimum). Gatekeeper
will block unsigned downloaded builds on first launch — right-click the .app
and choose *Open* once; signing/notarization arrives in a later phase.

> **Caveat for v0.1.0-beta:** the Linux and macOS code paths (libsecret /
> Keychain / VAAPI / VideoToolbox) have been validated by CI build only, not
> by a live end-to-end demo. Feedback from real Linux/Mac users is very
> welcome.

## Layout

```
src/
  OpenIPC.Viewer.Core/            netstandard2.1 — domain, no IO, no UI
  OpenIPC.Viewer.Infrastructure/  net9.0         — SQLite, secrets, config
  OpenIPC.Viewer.Video/           net9.0         — FFmpeg pipeline (FFmpeg.AutoGen + SkiaSharp)
  OpenIPC.Viewer.Devices/         net9.0         — ONVIF, Majestic HTTP
  OpenIPC.Viewer.App/             net9.0         — Avalonia views and viewmodels
  OpenIPC.Viewer.Desktop/         net9.0         — Win/Lin/Mac host, DI composition
tests/
  OpenIPC.Viewer.Core.Tests/      xUnit
  OpenIPC.Viewer.Video.Tests/     xUnit + MediaMTX integration
```

`App` references `Core` only. Infrastructure, Video, Devices are wired into `Desktop` via DI.

## Test fixture: MediaMTX

The video integration test and manual smoke depend on a local RTSP source. A
MediaMTX container is provided that synthesises a 1280×720@25 h264 test pattern
on demand at `rtsp://localhost:8554/test`.

```bash
docker compose -f tools/mediamtx/docker-compose.yml up -d
# ... do your testing ...
docker compose -f tools/mediamtx/docker-compose.yml down
```

The integration test auto-skips itself if the container isn't reachable.

## User data

Per-platform AppData root:

| OS | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\OpenIPC.Viewer\` |
| Linux | `$XDG_DATA_HOME/openipc-viewer/` (default `~/.local/share/openipc-viewer/`) |
| macOS | `~/Library/Application Support/OpenIPC.Viewer/` |

Inside the root:
```
logs/openipc-viewer-{date}.log    rolling daily, 7-day retention
appsettings.json                  optional override over the one shipped with the app
openipc-viewer.db                 SQLite database (cameras, recordings metadata, events)
secrets.bin / secrets.salt        encrypted credential fallback (used when no native keystore)
snapshots/{camera}/*.jpg          manual snapshots
recordings/                       MP4 segments (Linux/macOS may override via XDG_VIDEOS_DIR / ~/Movies)
```

Credentials live in the native keystore when available (Windows DPAPI / macOS
Keychain / Linux libsecret). The encrypted-file fallback is only used when
none of those is reachable.

## License

MIT. Bundled FFmpeg DLLs are LGPL — they ship as side-by-side shared libs and
can be replaced by the user.
