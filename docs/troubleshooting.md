# Troubleshooting

Honest list of things that bite first-run users.

## Windows

**SmartScreen warning on the .exe.** Unsigned builds trigger
*"Windows protected your PC"*. Click *More info → Run anyway*. Signed
installers arrive in a later release-polish phase.

**Bundled FFmpeg DLLs missing.** First-build run-time error usually
means `tools/fetch-ffmpeg.ps1` didn't run. Re-run from PowerShell:
```
powershell -ExecutionPolicy Bypass -File tools/fetch-ffmpeg.ps1
```

## Linux

**`FFmpeg native libraries failed to load for runtime linux-x64`.** The app
needs the FFmpeg **7.x** ABI (`libavcodec.so.61`), but no current Ubuntu LTS
ships FFmpeg 7 — 24.04 has 6.1 (`libavcodec.so.60`), 22.04 has 4.4 — so
`apt install ffmpeg` cannot satisfy it no matter how many `libav*` packages are
installed. The release archive **bundles** matching `n7.1` `.so`, so the fix is
to run the *extracted* `./OpenIPC.Viewer.Desktop` with its `runtimes/` folder
intact next to it (don't move the binary out on its own). Building from source?
Run `tools/fetch-ffmpeg-linux.sh` once to populate `runtimes/linux-x64/native/`.
To confirm what your system has: `ffmpeg -version | head -1` — anything below 7
is the wrong ABI and is ignored in favour of the bundled libs.

**VAAPI: `/dev/dri/renderD128` not found.** No GPU exposed to the user
session, or no DRI driver loaded. The app falls back to software decode;
that works but uses much more CPU. To enable VAAPI:
```
sudo apt install intel-media-va-driver   # Intel; or mesa-va-drivers for AMD
sudo usermod -aG render $USER            # then re-login
```

**libsecret missing.** Headless servers don't have D-Bus. The app falls
back to an AES-GCM file keyed off `/etc/machine-id` — works, just
strictly weaker than a real keyring.

## macOS

**Gatekeeper: *"OpenIPC.Viewer cannot be opened"*.** Right-click the
.app → *Open*. macOS remembers the choice after one confirm. Signing +
notarization land in a later release-polish phase.

**Keychain prompt on every credential read.** Means the app's
codesign identity changed between builds. Trust the keychain access
permanently once and it goes away (or re-add the camera).

## Android

**Recording stops when app goes to background.** Foreground service
notification was dismissed by the user. Android kills services whose
notification is swiped away. Don't dismiss it.

**Doze mode kills recordings.** On Android 12+ Doze respects foreground
services, but only if the user hasn't manually battery-optimised the
app. *Settings → Battery → Unrestricted*.

**App crashes on first recording.** Almost always the
`POST_NOTIFICATIONS` runtime permission was denied. Re-grant it via
*Settings → Apps → OpenIPC Viewer → Notifications*.

## iOS

**Recording stops when you leave the app.** Working as intended — Apple
doesn't grant 24/7 background recording to surveillance apps. For
always-on, run a relay (MediaMTX / Frigate) on a server.

**Files app doesn't show recordings.** `Info.plist` ships
`UIFileSharingEnabled = YES`; if files still don't appear, force-quit
and reopen the app once to nudge `LSSupportsOpeningDocumentsInPlace`.

## Cross-platform

**App opens with no cameras after upgrade.** Database lives in the
AppData root and survives upgrades. If you see empty: check whether
the `AppDataDir` path itself moved (e.g. Linux: did you set
`XDG_DATA_HOME` between launches?). The README has the path per OS.

**`'table X already exists'` migration error.** Old install with
mismatched schema. Wipe `openipc-viewer.db` from the AppData root and
restart — schemas are recreated.
