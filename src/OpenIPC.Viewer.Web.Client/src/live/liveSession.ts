// Live sessions that outlive the React tree that shows them.
//
// A session owns one <video> element plus its WebSocket/MediaSource pipeline
// (the proven Phase 20 MSE client, moved here verbatim from useLiveTile). Tiles
// no longer create videos: they BORROW a session's element, and hand it back on
// unmount. Because the pool lives above the router, navigating grid → camera →
// grid re-attaches the very same element with the socket still open, instead of
// restarting ffmpeg. Flipping grid pages back and forth gets the same benefit.
//
// Two rules make the borrowed element safe:
//   * it is always inside the document — the HTML spec pauses a media element
//     that is removed from a document, so a detached element would freeze;
//     released videos park in a hidden pool container instead.
//   * reparenting (pool ↔ cell) is a move within the same document, which the
//     spec's "still not in a document?" re-check tolerates. Playback continues.

// How long an unused session keeps streaming before it's closed. Long enough to
// cover a look at another page and back; short enough that walking away doesn't
// hold ffmpeg sessions open on the server forever.
const RETENTION_MS = 30_000

// Ceiling on retained (unused) sessions. Paging through an 82-camera layout
// would otherwise pile up idle streams faster than they expire. One full page of
// the largest grid (5×5) — anything less and leaving a big grid would evict the
// very tiles we're trying to keep alive.
const MAX_IDLE = 25

// How far behind the newest buffered frame playback may drift before we jump to
// the live edge, and how often we check.
const MAX_LAG_S = 5
const WATCHDOG_MS = 1000

type Listener = (status: string) => void

let poolContainer: HTMLElement | null = null
const sessions = new Set<LiveSession>()
// Insertion order of idle sessions = eviction order (oldest released first).
const idleOrder: LiveSession[] = []

// Called once by <LivePool/>: the always-mounted, invisible parent that released
// videos park in. Until it exists, sessions can't be created.
export function setPoolContainer(el: HTMLElement | null) {
  poolContainer = el
  if (el) for (const s of sessions) if (!s.video.parentElement) el.appendChild(s.video)
}

export class LiveSession {
  readonly video: HTMLVideoElement
  readonly cameraId: string

  private _status = 'connecting'
  private listeners = new Set<Listener>()
  private socket: WebSocket | null = null
  private objectUrl: string | null = null
  private watchdog: number | null = null
  private closed = false
  /** Borrowed by a mounted tile; false means idle and eligible for retention. */
  inUse = false
  idleTimer: number | null = null

  constructor(cameraId: string) {
    this.cameraId = cameraId
    const v = document.createElement('video')
    v.autoplay = true
    v.muted = true
    v.playsInline = true
    this.video = v
    poolContainer?.appendChild(v)
    this.connect(false)
    this.watchdog = window.setInterval(() => this.keepLive(), WATCHDOG_MS)
  }

  // Keeps the element pinned to the live edge and playing.
  //
  // Without this a tile can sit frozen forever: the fMP4 timeline carries the
  // ffmpeg session's own timestamps, so a viewer joining a stream that started
  // two minutes ago gets a buffer beginning at t=120s while currentTime is 0 —
  // outside every buffered range, so the element never has data to play and
  // autoplay never fires. The same gap opens whenever decoding stalls. Seeking
  // to the newest buffered frame fixes both, and drops accumulated latency: a
  // live view has no business being minutes behind.
  private keepLive() {
    const v = this.video
    const b = v.buffered
    if (!b.length) return
    const end = b.end(b.length - 1)
    if (v.currentTime < b.start(0) || end - v.currentTime > MAX_LAG_S)
      v.currentTime = Math.max(b.start(0), end - 0.3)
    if (v.paused) v.play().catch(() => undefined)
  }

  get status(): string {
    return this._status
  }

  subscribe(fn: Listener): () => void {
    this.listeners.add(fn)
    return () => this.listeners.delete(fn)
  }

  close() {
    this.closed = true
    if (this.watchdog !== null) window.clearInterval(this.watchdog)
    try {
      this.socket?.close()
    } catch {
      /* ignore */
    }
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl)
    this.video.removeAttribute('src')
    this.video.remove()
    this.listeners.clear()
  }

  private say(s: string) {
    if (this.closed) return
    this._status = s
    for (const fn of this.listeners) fn(s)
  }

  // transcode=false → copy remux (H.264). If the source turns out to be H.265
  // (or otherwise unplayable), reconnect once asking the server to re-encode.
  private connect(transcode: boolean) {
    const video = this.video
    if (!('MediaSource' in window)) {
      this.say('no MediaSource')
      return
    }
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const url = `${proto}://${location.host}/api/v1/cameras/${this.cameraId}/live${transcode ? '?transcode=1' : ''}`
    const ws = new WebSocket(url)
    ws.binaryType = 'arraybuffer'
    this.socket = ws
    const ms = new MediaSource()
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl)
    this.objectUrl = URL.createObjectURL(ms)
    video.src = this.objectUrl
    let sb: SourceBuffer | null = null
    // Explicit <ArrayBuffer> backing: appendBuffer wants a BufferSource over a
    // plain ArrayBuffer, not the widened ArrayBufferLike (TS 5.7+ typed arrays).
    const queue: Uint8Array<ArrayBuffer>[] = []
    let pending: Uint8Array<ArrayBuffer>[] = []
    let codec: string | null = null
    let msOpen = false
    let switching = false
    const say = (s: string) => this.say(s)

    // Reconnect asking the server to re-encode, once, when the source codec
    // can't play in MSE (H.265, or a profile the browser rejects).
    const retryTranscoded = () => {
      switching = true
      try {
        ws.close()
      } catch {
        /* ignore */
      }
      this.connect(true)
    }

    const trim = () => {
      try {
        if (sb && !sb.updating && sb.buffered.length && video.currentTime > 30)
          sb.remove(0, video.currentTime - 15)
      } catch {
        /* ignore */
      }
    }
    const pump = () => {
      if (!sb || sb.updating || !queue.length) return
      try {
        sb.appendBuffer(queue.shift()!)
      } catch {
        say('buffer error')
      }
      trim()
    }
    const setup = () => {
      if (sb || !msOpen || !codec || codec === 'hevc') return
      const mime = `video/mp4; codecs="${codec}"`
      if (!MediaSource.isTypeSupported(mime)) {
        if (!transcode) retryTranscoded()
        else say('unsupported ' + codec)
        return
      }
      sb = ms.addSourceBuffer(mime)
      sb.mode = 'segments'
      sb.addEventListener('updateend', pump)
      for (const x of pending) queue.push(x)
      pending = []
      pump()
    }

    ms.addEventListener('sourceopen', () => {
      msOpen = true
      setup()
    })
    say(transcode ? 'transcoding…' : 'connecting')
    ws.onopen = () => say(transcode ? 'live · transcoded' : 'live')
    ws.onclose = () => {
      if (!switching) say('ended')
    }
    ws.onerror = () => say('error')
    ws.onmessage = (e) => {
      const b = new Uint8Array(e.data as ArrayBuffer)
      if (!sb) {
        pending.push(b)
        if (codec === null) codec = findCodec(pending)
        if (codec === 'hevc' && !transcode) {
          retryTranscoded()
          return
        }
        setup()
        return
      }
      queue.push(b)
      pump()
    }
  }
}

// Hand a tile a session for this camera: reuse a retained one (instant resume,
// the socket never stopped), else start a fresh stream. A camera placed in two
// cells at once gets two sessions — one element can only be in one place — and
// the server's fan-out still runs a single ffmpeg for both.
export function acquire(cameraId: string): LiveSession {
  for (const s of sessions) {
    if (s.cameraId === cameraId && !s.inUse) {
      dropIdle(s)
      s.inUse = true
      return s
    }
  }
  const created = new LiveSession(cameraId)
  sessions.add(created)
  created.inUse = true
  return created
}

// Park the session: back to the hidden container, streaming, until either a tile
// picks it up again or the retention window closes it.
export function release(session: LiveSession) {
  session.inUse = false
  poolContainer?.appendChild(session.video)
  idleOrder.push(session)
  session.idleTimer = window.setTimeout(() => destroy(session), RETENTION_MS)
  while (idleOrder.length > MAX_IDLE) destroy(idleOrder[0])
}

function dropIdle(session: LiveSession) {
  if (session.idleTimer !== null) {
    window.clearTimeout(session.idleTimer)
    session.idleTimer = null
  }
  const i = idleOrder.indexOf(session)
  if (i >= 0) idleOrder.splice(i, 1)
}

function destroy(session: LiveSession) {
  dropIdle(session)
  sessions.delete(session)
  session.close()
}

// avcC → H.264 codec string; hvcC → 'hevc' sentinel (trigger transcode).
function findCodec(chunks: Uint8Array<ArrayBuffer>[]): string | null {
  let t = 0
  for (const c of chunks) t += c.length
  const d = new Uint8Array(t)
  let o = 0
  for (const c of chunks) {
    d.set(c, o)
    o += c.length
  }
  for (let i = 0; i + 8 < d.length; i++) {
    if (d[i] === 0x61 && d[i + 1] === 0x76 && d[i + 2] === 0x63 && d[i + 3] === 0x43)
      return 'avc1.' + hx(d[i + 5]) + hx(d[i + 6]) + hx(d[i + 7])
    if (d[i] === 0x68 && d[i + 1] === 0x76 && d[i + 2] === 0x63 && d[i + 3] === 0x43) return 'hevc'
  }
  return null
}

function hx(n: number): string {
  return n.toString(16).padStart(2, '0').toUpperCase()
}
