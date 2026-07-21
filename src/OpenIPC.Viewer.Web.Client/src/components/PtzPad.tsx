import { useCallback, useEffect, useRef, useState } from 'react'
import { api, type PtzPresetDto } from '../api'
import { useI18n } from '../i18n'

// Hold-to-move PTZ controls plus presets.
//
// The server is stateless: one POST /ptz/move = one ONVIF ContinuousMove with a
// self-stop timeout. So while a direction is held we re-send at REFRESH_MS, and
// on release we send /ptz/stop. If the tab dies mid-hold the refreshes stop and
// the camera halts on its own — the browser equivalent of PtzController's pump
// timeout, without any server-side session to leak.
const REFRESH_MS = 500
const MOVE_TIMEOUT_MS = 1200

type Dir = { panX?: number; tiltY?: number; zoom?: number }

export function PtzPad({ cameraId }: { cameraId: string }) {
  const { t } = useI18n()
  const [speed, setSpeed] = useState(0.6)
  const [presets, setPresets] = useState<PtzPresetDto[]>([])
  const [presetName, setPresetName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Held in refs so the interval callback always reads the live values without
  // re-subscribing (a re-render mid-drag must not restart the refresh loop).
  const timer = useRef<number | null>(null)
  const speedRef = useRef(speed)
  speedRef.current = speed

  const loadPresets = useCallback(async () => {
    try {
      setPresets(await api.ptzPresets(cameraId))
    } catch {
      // Presets are optional — plenty of firmwares answer GetPresets with a
      // fault. Movement still works, so don't shout about it.
      setPresets([])
    }
  }, [cameraId])

  const stop = useCallback(async () => {
    if (timer.current !== null) {
      window.clearInterval(timer.current)
      timer.current = null
    }
    try {
      await api.ptzStop(cameraId)
    } catch {
      setError(t('Ptz.Error'))
    }
  }, [cameraId, t])

  const start = useCallback(
    (dir: Dir) => {
      setError(null)
      const send = async () => {
        try {
          const s = speedRef.current
          await api.ptzMove(cameraId, {
            panX: (dir.panX ?? 0) * s,
            tiltY: (dir.tiltY ?? 0) * s,
            zoom: (dir.zoom ?? 0) * s,
            timeoutMs: MOVE_TIMEOUT_MS,
          })
        } catch {
          setError(t('Ptz.Error'))
          void stop()
        }
      }
      void send()
      if (timer.current !== null) window.clearInterval(timer.current)
      timer.current = window.setInterval(send, REFRESH_MS)
    },
    [cameraId, stop, t],
  )

  useEffect(() => {
    void loadPresets()
    // Leaving the page (or switching camera) while held must not keep panning.
    return () => {
      if (timer.current !== null) window.clearInterval(timer.current)
      void api.ptzStop(cameraId).catch(() => undefined)
    }
  }, [cameraId, loadPresets])

  // Pointer capture keeps the release event ours even if the finger slides off
  // the button — otherwise a drag-away would leave the camera moving.
  const hold = (dir: Dir) => ({
    onPointerDown: (e: React.PointerEvent<HTMLButtonElement>) => {
      e.preventDefault()
      e.currentTarget.setPointerCapture(e.pointerId)
      start(dir)
    },
    onPointerUp: () => void stop(),
    onPointerCancel: () => void stop(),
    onLostPointerCapture: () => void stop(),
  })

  const savePreset = async () => {
    const name = presetName.trim()
    if (!name) return
    setBusy(true)
    try {
      await api.ptzSavePreset(cameraId, name)
      setPresetName('')
      await loadPresets()
    } catch {
      setError(t('Ptz.Error'))
    } finally {
      setBusy(false)
    }
  }

  const run = async (action: Promise<unknown>) => {
    setBusy(true)
    try {
      await action
    } catch {
      setError(t('Ptz.Error'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="ptz">
      <div className="ptz-pad">
        <button {...hold({ panX: -1, tiltY: 1 })} title={t('Ptz.UpLeft')}>↖</button>
        <button {...hold({ tiltY: 1 })} title={t('Ptz.Up')}>↑</button>
        <button {...hold({ panX: 1, tiltY: 1 })} title={t('Ptz.UpRight')}>↗</button>
        <button {...hold({ panX: -1 })} title={t('Ptz.Left')}>←</button>
        <button onClick={() => void stop()} title={t('Ptz.Stop')}>■</button>
        <button {...hold({ panX: 1 })} title={t('Ptz.Right')}>→</button>
        <button {...hold({ panX: -1, tiltY: -1 })} title={t('Ptz.DownLeft')}>↙</button>
        <button {...hold({ tiltY: -1 })} title={t('Ptz.Down')}>↓</button>
        <button {...hold({ panX: 1, tiltY: -1 })} title={t('Ptz.DownRight')}>↘</button>
      </div>

      <div className="ptz-side">
        <div className="ptz-zoom">
          <button {...hold({ zoom: 1 })} title={t('Ptz.ZoomIn')}>＋</button>
          <span className="muted">{t('Ptz.Zoom')}</span>
          <button {...hold({ zoom: -1 })} title={t('Ptz.ZoomOut')}>－</button>
        </div>

        <label>
          {t('Ptz.Speed')}
          <input
            type="range"
            min={0.1}
            max={1}
            step={0.1}
            value={speed}
            onChange={(e) => setSpeed(Number(e.target.value))}
          />
        </label>

        <div className="ptz-presets">
          <span className="muted">{t('Ptz.Presets')}</span>
          {presets.length === 0 && <span className="muted">—</span>}
          {presets.map((p) => (
            <span key={p.token} className="row" style={{ gap: 4 }}>
              <button
                className="preset"
                disabled={busy}
                onClick={() => void run(api.ptzGotoPreset(cameraId, p.token))}
              >
                {p.name}
              </button>
              <button
                className="danger"
                disabled={busy}
                title={t('Ptz.PresetDelete')}
                onClick={() =>
                  void run(
                    api.ptzDeletePreset(cameraId, p.token).then(loadPresets),
                  )
                }
              >
                ×
              </button>
            </span>
          ))}
          <span className="row" style={{ gap: 4 }}>
            <input
              value={presetName}
              placeholder={t('Ptz.PresetName')}
              onChange={(e) => setPresetName(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && void savePreset()}
            />
            <button disabled={busy || !presetName.trim()} onClick={() => void savePreset()}>
              {t('Ptz.PresetSave')}
            </button>
          </span>
        </div>

        {error && <span className="err">{error}</span>}
      </div>
    </div>
  )
}
