import { useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../api'
import { useI18n } from '../i18n'
import { Icon } from './Icon'
import { canCaptureMic, startTalking, type TalkHandle } from '../live/talkSession'

// Push-to-talk: held, not toggled. A talk button that latches is a button that
// gets left on, and the thing on the other end is a speaker in a room with
// people in it.
export function TalkButton({ cameraId }: { cameraId: string }) {
  const { t } = useI18n()
  const [talking, setTalking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const handle = useRef<TalkHandle | null>(null)
  const secure = canCaptureMic()

  // Never leave the camera's speaker open because a route changed.
  useEffect(() => () => void handle.current?.stop(), [])

  const begin = async () => {
    if (talking || busy) return
    setBusy(true)
    setMessage(null)
    try {
      // Probe over HTTP first: a refused WebSocket upgrade reaches the browser
      // as a bare error with no status, so this is the only place the real
      // reason ("no speaker", "someone else is talking") can come from.
      const probe = await api.talkProbe(cameraId)
      if (probe.supported === false) {
        setMessage(t('Talk.Unsupported'))
        return
      }
      if (probe.busy) {
        setMessage(t('Talk.Busy'))
        return
      }
      handle.current = await startTalking(cameraId, (reason) => {
        handle.current = null
        setTalking(false)
        if (reason) setMessage(t('Talk.Failed'))
      })
      setTalking(true)
    } catch (e) {
      if (e instanceof ApiError && e.code === 'talk_unsupported') setMessage(t('Talk.Unsupported'))
      else if (e instanceof DOMException) setMessage(t('Talk.NoMic'))
      else setMessage(t('Talk.Failed'))
    } finally {
      setBusy(false)
    }
  }

  const end = async () => {
    const current = handle.current
    handle.current = null
    setTalking(false)
    await current?.stop()
  }

  if (!secure) {
    return (
      <p className="muted" style={{ fontSize: 12 }}>
        <Icon name="mic" size={13} /> {t('Talk.NeedsHttps')}
      </p>
    )
  }

  return (
    <div className="row" style={{ flexWrap: 'wrap' }}>
      <button
        className={'row talk' + (talking ? ' on' : '')}
        disabled={busy}
        // Pointer events rather than mouse/touch pairs, and pointer capture so
        // sliding a finger off the button still ends the transmission.
        onPointerDown={(e) => {
          // Capture is a nicety, not a precondition: it throws for a pointer
          // that is no longer active (a fast enough tap), and losing the press
          // because of that would be worse than losing the capture.
          try {
            e.currentTarget.setPointerCapture(e.pointerId)
          } catch { /* no capture — pointerup still ends the transmission */ }
          void begin()
        }}
        onPointerUp={() => void end()}
        onPointerCancel={() => void end()}
      >
        <Icon name="mic" size={15} />
        {talking ? t('Talk.Talking') : t('Talk.Hold')}
      </button>
      {message && <span className="muted">{message}</span>}
    </div>
  )
}
