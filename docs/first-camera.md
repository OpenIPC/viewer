# Adding your first camera

Three paths, listed cheapest-to-most-manual. All three end in the same camera
editor, which checks the camera before it is saved.

## 1. Network discovery (LAN scan)

Library → *Find on network* (also on the first-run welcome screen, and at the
bottom of the *Add camera* dialog). The dialog starts a **quick scan** the
moment it opens: two passive sources at once — ONVIF WS-Discovery (multicast
`239.255.255.250:3702`) and mDNS — merged into one list, deduplicated per
device. Cameras with an ONVIF responder show up within ~5 s; OpenIPC devices
without ONVIF are recognised by their web fingerprint.

Each row says what the device is (*OpenIPC camera*, *ONVIF camera*, or just
*answers RTSP* / *answers HTTP*), its model and address, and has its own
*Add*. Cameras you already have are listed separately under *Already in
library*, so they don't crowd the new ones.

*Add* opens the camera editor for that address and **connects straight away**
(see [Manual](#2-manual) below for what Connect does). The login you type
there is remembered for the rest of the session and tried on the next camera,
and closing the editor brings you back to the same scan list — so several
cameras from one scan are a few clicks each.

If the quick scan finds nothing, the empty state points at the two ways on:
a deep scan, or entering the address yourself.

### Deep scan and where it sweeps

WS-Discovery and mDNS are multicast, so they only ever reach the local
link. *Deep scan* adds an active sweep — a TCP knock on each host — which
is how a camera that answers neither, or sits on a subnet multicast can't
cross, gets found.

Open the *Deep scan* footer at the bottom of the dialog. It lists the
subnets it can sweep, read from the machine's routing table:

- Subnets **this machine is on** (*this network*) are ticked by default.
- Subnets reachable only **through a route** (*through a VPN or route* —
  another VLAN, or a VPN / mesh tunnel) are listed but left unticked — tick the ones you mean to
  sweep. This is the case that needs no typing: the camera's subnet shows
  up on its own, you just tick it.

The **IP range** box below the list is for anything the routing table
doesn't show — a subnet with no route of its own, or a single address. What
you type is **combined with** the ticked subnets, not used instead of them.
Accepted:

| Typed                         | Sweeps                        |
|-------------------------------|-------------------------------|
| `192.168.1.0/24`              | `192.168.1.1` … `192.168.1.254` |
| `192.168.1.10-192.168.1.200`  | that span                     |
| `192.168.1.10-200`            | the same, last octet only     |
| `192.168.1.64`                | one host                      |

Several at once, separated by commas or spaces. The running total is shown
before you scan; at most 4096 addresses per scan, so if the ticked subnets
plus the typed range exceed that, untick one or narrow the range. Press
*Scan deeper*; progress and the number found so far update live, and *Stop*
ends it early. Scan results, the ticked subnets and the typed range are kept
for the rest of the session, so reopening the dialog doesn't start over.

On the [web console](web-server.md) the typed range must stay inside a
private network (`10/8`, `172.16/12`, `192.168/16`) — a server accepts a
range from any Manage user, so it isn't allowed to be aimed at the wider
internet or at its own loopback.

If discovery turns up nothing:
- Your router is blocking multicast (check WiFi AP settings — some block
  it on guest networks by default).
- The camera is on a different VLAN — tick that subnet in the Deep scan
  list (or type its range), then scan again. It has to be routable from
  here; discovery can't cross a firewall that drops the traffic.
- ONVIF is disabled in the camera config (Majestic ships with it disabled
  in some firmware revisions — flip `service.onvif.enabled = true`).

## 2. Manual

Library → *Add*. The dialog asks for two things first:

- **Address** — an IP, `ip:port`, or a whole `rtsp://` link. A pasted link is
  split up for you: host, port, path and any `user:pass@` land in their own
  fields.
- **Username / password** — OpenIPC ships with `root` / `12345`. Credentials
  are kept in the platform keystore (DPAPI / Keychain / libsecret) or an
  AES-GCM file fallback, never inside the saved RTSP URL.

Then press **Connect**. The editor works out whether it is an OpenIPC
(Majestic) or ONVIF camera, fills in the main and sub streams, the HTTP /
ONVIF / SSH ports and a name, and shows a live frame with the resolution and
whether there is audio. If it can't, a card says which of these it was:

| Card | Usually means |
|---|---|
| *The camera doesn't respond* | wrong address, or the camera isn't on a network this machine can reach |
| *Wrong username or password* / *needs a username and password* | fix the login and press Connect again |
| *The camera answers, but the video stream didn't open* | the stream path is wrong — see below |

Everything else sits under **Advanced**, one collapsible row each with a
one-line summary: *Streams* (main / sub RTSP URLs, plus *Vendor templates* for
cameras that don't use OpenIPC's paths), *Ports*, *SSH access* (leave empty to
reuse the main login), *Grid quality* (auto SD/HD, always HD, always SD) and
*AI detection*. For OpenIPC mainline the streams are `rtsp://<host>:554/0`
(main) and `/1` (sub).

A camera can be put into a group from the same dialog, including a new group
made on the spot.

## 3. QR code

Library → the QR button (also offered in the first-run welcome dialog and at
the bottom of the *Add camera* dialog). Pick a
saved QR **image** — a screenshot, a photo, or the sticker sheet a camera
shipped with — and the app decodes it and opens the camera editor pre-filled,
so you review the fields before saving. Three payload shapes are understood:

| Payload | Example |
|---|---|
| RTSP URL | `rtsp://admin:pass@192.168.1.50:554/0` |
| JSON object | `{"name":"Gate","host":"192.168.1.50","rtsp":"rtsp://192.168.1.50/0","onvif":8000,"user":"admin","password":"…"}` |
| App URI | `openipc-viewer://camera?host=192.168.1.50&rtsp=rtsp://192.168.1.50/0&user=admin` |

Credentials found in the payload land in the editor's username / password
fields and are stored in the keystore — they are never kept inside the saved
RTSP URL. Scanning through the phone's camera (rather than picking an image)
is not implemented yet.

## Common issues

- **"The camera answers, but the video stream didn't open"** — wrong RTSP
  path. Camera vendors love to pick different defaults (`/cam/realmonitor`,
  `/stream1`, `/h264`). Try *Advanced → Streams → Vendor templates*, or check
  the vendor docs.
- **Auth loops** — the URI takes plain user/password but credentials live
  in the keystore separately. Setting them in both places is fine; just
  the keystore copy is preferred (URI auth shows up in logs).
- **VAAPI permission denied** (Linux) — `usermod -aG render $USER` then
  re-login. The app falls back to software decode but eats more CPU.
- **Android: missing notification permission** — recording shows a
  notification while it runs. On Android 13+ the runtime permission
  prompt fires the first time you start a recording.
