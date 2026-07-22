// Browser side of push-to-talk: microphone → 16-bit PCM → WebSocket → the
// server's RTSP backchannel.
//
// The audio graph runs at the rate the CAMERA negotiated, which the server sends
// as the socket's first message. An AudioContext created with that sampleRate
// resamples the microphone for us, so there is no resampler to write and no
// chance of shipping 48 kHz samples that the camera plays back six times too
// fast.
//
// The worklet is built from a blob rather than a separate module file: it is
// fifteen lines, it must not drift away from the framing the server expects, and
// this keeps it out of the bundler's asset pipeline entirely.
const WORKLET_SOURCE = `
class PcmChunker extends AudioWorkletProcessor {
  constructor(options) {
    super()
    // One RTP packet per chunk. 20 ms is what G.711 telephony uses and what the
    // camera expects to be fed; the render quantum (128 frames) doesn't divide
    // into it evenly, so samples are carried across quanta.
    this.size = options.processorOptions.chunkSamples
    this.buffer = new Int16Array(this.size)
    this.filled = 0
  }
  process(inputs) {
    const channel = inputs[0] && inputs[0][0]
    if (!channel) return true
    for (let i = 0; i < channel.length; i++) {
      // Clamp before scaling: a sample slightly outside [-1,1] would wrap around
      // to the opposite extreme and click.
      const s = Math.max(-1, Math.min(1, channel[i]))
      this.buffer[this.filled++] = s < 0 ? s * 0x8000 : s * 0x7fff
      if (this.filled === this.size) {
        this.port.postMessage(this.buffer.slice())
        this.filled = 0
      }
    }
    return true
  }
}
registerProcessor('pcm-chunker', PcmChunker)
`

export type TalkHandle = {
  stop: () => Promise<void>
}

// True when the browser will even hand out a microphone. getUserMedia only
// exists in a secure context, so a console served over plain HTTP on a LAN has
// no way to talk — worth saying out loud rather than failing at the press.
export function canCaptureMic(): boolean {
  return window.isSecureContext && typeof navigator.mediaDevices?.getUserMedia === 'function'
}

// Opens the socket, waits for the negotiated format, starts capturing. Resolves
// once audio is actually flowing; rejects if any step fails, having cleaned up
// whatever it already opened.
export async function startTalking(cameraId: string, onEnded: (reason?: string) => void): Promise<TalkHandle> {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  const socket = new WebSocket(`${proto}://${location.host}/api/v1/cameras/${cameraId}/talk/stream`)
  socket.binaryType = 'arraybuffer'

  let stream: MediaStream | null = null
  let context: AudioContext | null = null
  let stopped = false

  const cleanup = async () => {
    if (stopped) return
    stopped = true
    stream?.getTracks().forEach((t) => t.stop())
    try {
      await context?.close()
    } catch { /* already closing */ }
    if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) socket.close()
  }

  try {
    const format = await new Promise<{ sampleRate: number }>((resolve, reject) => {
      // A refused upgrade (no permission, camera busy, no backchannel) reaches
      // the browser as a bare error with no status — the caller probes over HTTP
      // first, which is where the real reason comes from.
      socket.onerror = () => reject(new Error('socket'))
      socket.onclose = () => reject(new Error('closed'))
      socket.onmessage = (e) => {
        if (typeof e.data !== 'string') return
        resolve(JSON.parse(e.data) as { sampleRate: number })
      }
    })

    stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
    })

    context = new AudioContext({ sampleRate: format.sampleRate })
    const blob = new Blob([WORKLET_SOURCE], { type: 'application/javascript' })
    const url = URL.createObjectURL(blob)
    try {
      await context.audioWorklet.addModule(url)
    } finally {
      URL.revokeObjectURL(url)
    }

    const source = context.createMediaStreamSource(stream)
    const chunker = new AudioWorkletNode(context, 'pcm-chunker', {
      numberOfOutputs: 0,
      processorOptions: { chunkSamples: Math.round(format.sampleRate / 50) },
    })
    chunker.port.onmessage = (e) => {
      if (socket.readyState === WebSocket.OPEN) socket.send((e.data as Int16Array).buffer)
    }
    source.connect(chunker)

    // From here on a socket that dies is the end of the conversation, not an
    // error to reject with — the caller is already talking.
    socket.onclose = () => {
      void cleanup()
      onEnded()
    }
    socket.onerror = () => {
      void cleanup()
      onEnded('socket')
    }

    return { stop: cleanup }
  } catch (e) {
    await cleanup()
    throw e
  }
}
