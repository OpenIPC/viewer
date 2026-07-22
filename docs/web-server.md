# Self-hosting the OpenIPC Viewer web server

**English** · [Русский](web-server.ru.md)

The desktop app ships a headless **web server** mode: the *same binary*, launched
with `--server-only`, serves a browser console for your OpenIPC cameras over your
LAN. No cloud, no extra services — one admin password and a port.

* **Live grid** (1 / 4 / 9 / 16 / 25) with saved layouts and paging, per-camera
  live view, fullscreen tiles. Video keeps playing while you move between pages.
* **Camera management** — add / edit / delete, groups, import/export config backup.
* **Find cameras on the network** — ONVIF, mDNS and an opt-in subnet sweep, then
  add what you found without leaving the browser.
* **PTZ** — pan/tilt/zoom and presets for cameras that support it.
* **Snapshots** — grab a still from any camera and download it.
* **Archive** — record a camera from the browser, then browse by calendar, play,
  download, and export a marked fragment; seeking works because playback is a
  ranged file response. Both heads write into the same folders and index, so a
  clip recorded here shows up in the desktop app too.
* **Users and permissions** — accounts with permission flags and, if you like, a
  per-user subset of cameras.
* **H.264 over WebSocket** (fMP4 + MSE); H.265 is transcoded on the fly. One
  ffmpeg session is shared across all viewers of a camera.
* Works from any browser on the network — phone, tablet, another PC.
* English / Russian, dark Console skin matching the desktop app.

It reads and writes the **same database as the desktop app**, so cameras you add
in one show up in the other.

---

## Quick start (LAN)

1. **Download** the release archive for your OS and unzip it
   (`openipc-viewer-win-x64.zip`, `-linux-x64.tar.gz`, `-osx-arm64.tar.gz`),
   or [build from source](#build-from-source).

2. **Pick an admin password** (otherwise a random one is generated and printed to
   the log on every start — fine for a quick test, not for a stable deployment).

3. **Run it, bound to the LAN:**

   **Linux / macOS**
   ```bash
   export OPENIPC_WEB_ADMIN_PASSWORD='choose-a-strong-one'
   ./OpenIPC.Viewer.Desktop --server-only --lan --port 8787
   ```

   **Windows (PowerShell)**
   ```powershell
   $env:OPENIPC_WEB_ADMIN_PASSWORD = 'choose-a-strong-one'
   .\OpenIPC.Viewer.Desktop.exe --server-only --lan --port 8787
   ```

4. **Open** `http://<this-machine-ip>:8787` from any device on the network and log
   in as **`admin`** with the password you set.

Without `--lan` the server binds to `127.0.0.1` only (reachable from the same
machine) — the safe default. `--lan` binds `0.0.0.0` and prints a warning, because
there is no TLS at this layer; for anything beyond your trusted LAN, put it
[behind a reverse proxy](#expose-on-a-domain-https).

### Flags

| Flag              | Meaning                                             | Default |
|-------------------|-----------------------------------------------------|---------|
| `--server-only`   | Run headless as a web server (no desktop GUI)       | —       |
| `--port <n>`      | TCP port to listen on                               | `8787`  |
| `--lan`           | Bind `0.0.0.0` (reachable from the LAN)             | off (localhost only) |

| Env var                       | Meaning                                          |
|-------------------------------|--------------------------------------------------|
| `OPENIPC_WEB_ADMIN_PASSWORD`  | Built-in admin password. Unset → random, logged on start. |
| `OPENIPC_WEB_SEGMENT_SECONDS` | Length of one recording segment. Default 600 (10 min).   |

---

## Adding cameras

Four ways, all landing in the same database:

* **Find them on the network** — *Discovery → Scan*. Passive sources (ONVIF
  WS-Discovery, mDNS) always run; *Deep scan* additionally sweeps the local
  subnet, which finds cameras that announce nothing but looks like a port scan
  to anyone watching the network. Pick a device, enter its login, *Probe* to read
  the real RTSP URL and capabilities off the camera, then *Add*.
* **In the web UI** — *Cameras → ＋ Add camera*.
* **Import a backup** — *System → Import* a JSON file exported from the desktop app
  or another instance (*System → Export config* produces one; camera passwords are
  never included in the export).
* **Reuse the desktop app's data** — run the server on a machine that already has
  the desktop app configured; it uses the same on-disk database:

  | OS      | Database path                                                        |
  |---------|----------------------------------------------------------------------|
  | Windows | `%LOCALAPPDATA%\OpenIPC.Viewer\openipc-viewer.db`                     |
  | Linux   | `~/.local/share/openipc-viewer/openipc-viewer.db` (`$XDG_DATA_HOME`) |
  | macOS   | `~/Library/Application Support/OpenIPC.Viewer/openipc-viewer.db`      |

  Camera credentials live in the OS secret store, not in this file.

---

## Users and permissions

Out of the box there is one account: the built-in administrator from
`OPENIPC_WEB_ADMIN_PASSWORD`. *Users → ＋ Add user* creates more, each with:

| Permission     | Grants                                                        |
|----------------|---------------------------------------------------------------|
| `watch live`   | see the camera list and live video                            |
| `PTZ`          | move the camera and manage its presets                        |
| `export`       | cut and download a fragment of a recording                     |
| `manage`       | everything that changes the installation — cameras, groups, layouts, discovery, backups, sessions, users |

and, optionally, a **subset of cameras**: leave *All cameras* on for full access,
or tick the ones a user may see. Cameras outside the subset are invisible —
absent from the list, from the grid, and from the API.

The roster lives next to the database as `web-users.json` (passwords are stored
as PBKDF2 hashes, never in the clear). The built-in administrator keeps working
even after you add users — it is the way back in if the last `manage` account is
lost, so keep its password safe.

> Permission changes take effect the next time that user signs in.

---

## ffmpeg

Live video needs ffmpeg. Resolution order: a bundled binary next to the app
(`runtimes/<rid>/native/ffmpeg`) first, then `ffmpeg` on `PATH`.

* **Windows** — bundled in the release archive. Nothing to install.
* **Linux** — bundled in the release archive; or `sudo apt install ffmpeg`.
* **macOS** — install via `brew install ffmpeg` (not bundled).

---

## Expose on a domain (HTTPS)

The server speaks plain HTTP. To reach it over the internet or from an untrusted
network, terminate TLS at a reverse proxy and forward to the local port. The server
already honours `X-Forwarded-*` from a loopback proxy, and the session cookie
becomes `Secure` automatically once requests arrive over HTTPS.

**Caddy** (automatic Let's Encrypt certificates, WebSockets pass through as-is):

```caddy
cameras.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

Run the app **without** `--lan` (localhost bind) when the proxy is on the same host
— nothing else needs network exposure. If the proxy runs on a *different* host,
bind with `--lan` and pass the proxy's IP so its forwarded headers are trusted
(otherwise cross-origin/CSRF checks reject proxied requests):

```bash
./OpenIPC.Viewer.Desktop --server-only --lan --port 8787
# and set the trusted proxy IP(s) — see WebServerOptions.TrustedProxies
```

**nginx** equivalent needs the WebSocket upgrade headers:

```nginx
location / {
    proxy_pass         http://127.0.0.1:8787;
    proxy_set_header   Host $host;
    proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;
    proxy_http_version 1.1;
    proxy_set_header   Upgrade    $http_upgrade;   # live-video WebSocket
    proxy_set_header   Connection "upgrade";
}
```

---

## Run it as a service

**Linux (systemd)** — `/etc/systemd/system/openipc-web.service`:

```ini
[Unit]
Description=OpenIPC Viewer web server
After=network.target

[Service]
Environment=OPENIPC_WEB_ADMIN_PASSWORD=choose-a-strong-one
ExecStart=/opt/openipc-viewer/OpenIPC.Viewer.Desktop --server-only --lan --port 8787
Restart=on-failure
User=openipc

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now openipc-web
```

**Windows** — run at logon via Task Scheduler, or wrap it as a service with
[NSSM](https://nssm.cc/). Set `OPENIPC_WEB_ADMIN_PASSWORD` as a machine environment
variable so the service picks it up.

---

## Security notes

* **Localhost by default.** Network exposure (`--lan`) is opt-in and warned about.
* **No built-in TLS** — use a reverse proxy for HTTPS (above).
* **Accounts and permissions.** Login is rate-limited (5/min/IP); sessions are
  opaque tokens in an `HttpOnly`, `SameSite=Strict` cookie with a 12-hour sliding
  expiry. Every request is authorized on the server — the UI hides what you may
  not do, but hiding is not the boundary.
* **The web server is a separate door to your cameras.** Anyone who reaches the
  port with an account gets whatever their permissions allow, from any machine.
  Bind to the LAN deliberately, and give each person only the cameras they need.
* **Config export** never contains camera passwords.
* Health check: `GET /healthz` (no auth) returns `{"status":"ok","version":"…"}`.

---

## Build from source

```bash
dotnet publish src/OpenIPC.Viewer.Desktop -c Release -r linux-x64 \
  --self-contained true -o publish
# Node is required — the build compiles the React web UI and embeds it.
./publish/OpenIPC.Viewer.Desktop --server-only --lan --port 8787
```

Swap `linux-x64` for `win-x64` / `osx-arm64` as needed.

---

## How it's built — where does the React app go?

There is **no separate front-end to deploy**. The browser UI is a React + Vite
single-page app whose *compiled output is embedded inside the server binary* at
build time, so at runtime there is no Node, no `dist/` to copy, and no static-file
server to run — just the one executable.

```
src/OpenIPC.Viewer.Web.Client   React + TypeScript + Vite  (source)
          │  dotnet build / publish runs an MSBuild target →
          ▼  npm run build   (Vite emits index.html + hashed assets)
   OpenIPC.Viewer.Web/wwwroot   build output
          │  embedded as resources into the assembly →
          ▼
   OpenIPC.Viewer.Web.dll       the compiled SPA lives *inside* this DLL
          │  at runtime Kestrel serves it from memory →
          ▼
   GET /            → embedded index.html
   GET /assets/*    → embedded JS / CSS
   GET /grid, …     → SPA client routes (fallback to index.html)
```

Node is therefore a **build-time-only** dependency (CI installs it; a source build
needs it). A downloaded release archive already has the React app baked into the
binary. This embed-in-the-DLL approach also means the web UI travels with the
server assembly no matter which host loads it, rather than depending on a
framework-specific static-assets pipeline.
