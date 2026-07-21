import { useEffect, useState } from 'react'

// React wrapper around the proven Phase 20 MSE+WS live client (was
// window.openipcLive.start in App.razor). Opens the camera's fMP4/WS stream,
// reads the codec from the avcC/hvcC init box (any H.264 profile plays; H.265
// reconnects once asking the server to transcode), appends to a MediaSource and
// trims. Cleanup closes the socket on unmount.
//
// The whole point of Phase 21: a tile mounts once per cameraId and survives grid
// layout changes (React keeps the same keyed element), so the WS/ffmpeg session
// is NOT torn down when switching 1<->4<->9. This hook owns exactly one session
// for the life of the mounted <video>.
export function useLiveTile(video: HTMLVideoElement | null, cameraId: string): string {
  const [status, setStatus] = useState('connecting')

  useEffect(() => {
    if (!video) return
    if (!('MediaSource' in window)) {
      setStatus('no MediaSource')
      return
    }

    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    let active: WebSocket | null = null
    let closed = false
    const say = (s: string) => {
      if (!closed) setStatus(s)
    }

    // transcode=false → copy remux (H.264). If the source turns out to be H.265
    // (or otherwise unplayable), reconnect once asking the server to re-encode.
    function connect(transcode: boolean) {
      const url = `${proto}://${location.host}/api/v1/cameras/${cameraId}/live${transcode ? '?transcode=1' : ''}`
      const ws = new WebSocket(url)
      ws.binaryType = 'arraybuffer'
      active = ws
      const ms = new MediaSource()
      video!.src = URL.createObjectURL(ms)
      let sb: SourceBuffer | null = null
      // Explicit <ArrayBuffer> backing: appendBuffer wants a BufferSource over a
      // plain ArrayBuffer, not the widened ArrayBufferLike (TS 5.7+ typed arrays).
      const queue: Uint8Array<ArrayBuffer>[] = []
      let pending: Uint8Array<ArrayBuffer>[] = []
      let codec: string | null = null
      let msOpen = false
      let switching = false
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
            switching = true
            try {
              ws.close()
            } catch {
              /* ignore */
            }
            connect(true)
            return
          }
          setup()
          return
        }
        queue.push(b)
        pump()
      }
      function setup() {
        if (sb || !msOpen || !codec || codec === 'hevc') return
        const mime = `video/mp4; codecs="${codec}"`
        if (!MediaSource.isTypeSupported(mime)) {
          if (!transcode) {
            switching = true
            try {
              ws.close()
            } catch {
              /* ignore */
            }
            connect(true)
          } else {
            say('unsupported ' + codec)
          }
          return
        }
        sb = ms.addSourceBuffer(mime)
        sb.mode = 'segments'
        sb.addEventListener('updateend', pump)
        for (const x of pending) queue.push(x)
        pending = []
        pump()
      }
      function pump() {
        if (!sb || sb.updating || !queue.length) return
        try {
          sb.appendBuffer(queue.shift()!)
        } catch {
          say('buffer error')
        }
        trim()
      }
      function trim() {
        try {
          if (sb && !sb.updating && sb.buffered.length && video!.currentTime > 30)
            sb.remove(0, video!.currentTime - 15)
        } catch {
          /* ignore */
        }
      }
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

    connect(false)
    return () => {
      closed = true
      try {
        active?.close()
      } catch {
        /* ignore */
      }
    }
  }, [video, cameraId])

  return status
}
