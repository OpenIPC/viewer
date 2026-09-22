import { useCallback, useEffect, useRef, useState } from 'react'
import { api, type PtzCapabilitiesDto, type PtzPresetDto } from '../api'
import { useI18n } from '../i18n'
import { Icon } from './Icon'

// Hold-to-move PTZ controls plus presets.
//
// The server is stateless: one POST /ptz/move = one ONVIF ContinuousMove with a
// self-stop timeout. So while a direction is held we re-send at REFRESH_MS, and
// on release we send /ptz/stop. If the tab dies mid-hold the refreshes stop and
// the camera halts on its own — the browser equivalent of PtzController's pump
// timeout, without any server-side session to leak.
const REFRESH_MS = 500
const MOVE_TIMEOUT_MS = 1200

// How far one nudge moves, normalized. On a camera that reports its relative
// space in field-of-view units this is a sixth of the frame.
const STEP = 0.16

// On a step-capable axis the sweep does not begin until a press has lasted this
// long. A shorter press was never going to sweep anywhere useful, so it lands
// as exactly one step — and because the sweep never started, there is no stop
// racing the step on release.
const TAP_MS = 220

type Dir = { panX?: number; tiltY?: number; zoom?: number }

export function PtzPad({ cameraId }: { cameraId: string }) {
  const { t } = useI18n()
  const [speed, setSpeed] = useState(0.6)
  const [presets, setPresets] = useState<PtzPresetDto[]>([])
  const [presetName, setPresetName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [caps, setCaps] = useState<PtzCapabilitiesDto | null>(null)

  // Held in refs so the interval callback always reads the live values without
  // re-subscribing (a re-render mid-drag must not restart the refresh loop).
  const timer = useRef<number | null>(null)
  const speedRef = useRef(speed)
  speedRef.current = speed
  // One press is one of: 'pending' (step-capable, inside the tap window),
  // 'sweeping' (continuous move running), 'idle'.
  const press = useRef<'idle' | 'pending' | 'sweeping'>('idle')
  const holdTimer = useRef<number | null>(null)

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

  // One nudge. Held buttons sweep, a tap frames — the same split the desktop
  // keypad makes, and the only way to land on a doorway at full zoom.
  const step = useCallback(
    (dir: Dir) => {
      void api
        .ptzStep(cameraId, {
          panX: (dir.panX ?? 0) * STEP,
          tiltY: (dir.tiltY ?? 0) * STEP,
          zoom: (dir.zoom ?? 0) * STEP,
          speed: speedRef.current,
        })
        .catch(() => setError(t('Ptz.Error')))
    },
    [cameraId, t],
  )

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
    let cancelled = false
    api
      .ptzCapabilities(cameraId)
      .then((c) => { if (!cancelled) setCaps(c) })
      // A camera that will not describe itself keeps the plain Stop button.
      .catch(() => { if (!cancelled) setCaps(null) })
    return () => { cancelled = true }
  }, [cameraId])

  useEffect(() => {
    void loadPresets()
    // Leaving the page (or switching camera) while held must not keep panning.
    return () => {
      if (timer.current !== null) window.clearInterval(timer.current)
      if (holdTimer.current !== null) window.clearTimeout(holdTimer.current)
      void api.ptzStop(cameraId).catch(() => undefined)
    }
  }, [cameraId, loadPresets])

  // A release inside the tap window sends one step; a longer press swept, so
  // it sends one stop. The two never race because the sweep does not start
  // until the window has passed.
  const endPress = (dir: Dir | null) => {
    if (holdTimer.current !== null) {
      window.clearTimeout(holdTimer.current)
      holdTimer.current = null
    }
    const mode = press.current
    press.current = 'idle'
    if (mode === 'sweeping') void stop()
    // dir is null when the pointer was cancelled or captured away — nothing
    // moved yet, and a step the user did not release on would be a surprise.
    else if (mode === 'pending' && dir) step(dir)
  }

  // Pointer capture keeps the release event ours even if the finger slides off
  // the button — otherwise a drag-away would leave the camera moving.
  const hold = (dir: Dir) => ({
    onPointerDown: (e: React.PointerEvent<HTMLButtonElement>) => {
      e.preventDefault()
      e.currentTarget.setPointerCapture(e.pointerId)
      const canStep = dir.zoom ? caps?.relativeZoom : caps?.relativePanTilt
      if (!canStep) {
        // No step on this axis: the hold-to-sweep behaviour, immediately, as
        // before capabilities existed.
        press.current = 'sweeping'
        start(dir)
        return
      }
      press.current = 'pending'
      holdTimer.current = window.setTimeout(() => {
        press.current = 'sweeping'
        holdTimer.current = null
        start(dir)
      }, TAP_MS)
    },
    onPointerUp: () => endPress(dir),
    onPointerCancel: () => endPress(null),
    onLostPointerCapture: () => endPress(null),
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

  // Before capabilities load everything shows, as it always did; once they
  // have, an axis the camera can serve neither way loses its keys instead of
  // keeping buttons that can only fail.
  const showPad = !caps || caps.relativePanTilt || caps.continuousPanTilt
  const showZoom = !caps || caps.relativeZoom || caps.continuousZoom

  return (
    <div className="ptz">
      {showPad && (
      <div className="ptz-pad">
        <button {...hold({ panX: -1, tiltY: 1 })} title={t('Ptz.UpLeft')}><Icon name="arrowUpLeft" size={18} /></button>
        <button {...hold({ tiltY: 1 })} title={t('Ptz.Up')}><Icon name="chevronUp" size={18} /></button>
        <button {...hold({ panX: 1, tiltY: 1 })} title={t('Ptz.UpRight')}><Icon name="arrowUpRight" size={18} /></button>
        <button {...hold({ panX: -1 })} title={t('Ptz.Left')}><Icon name="chevronLeft" size={18} /></button>
        {caps?.home ? (
          <button
            onClick={() => void api.ptzHome(cameraId, speed).catch(() => setError(t('Ptz.Error')))}
            title={t('Ptz.Home')}
          >
            <Icon name="home" size={18} />
          </button>
        ) : (
          <button onClick={() => void stop()} title={t('Ptz.Stop')}><Icon name="stop" size={18} /></button>
        )}
        <button {...hold({ panX: 1 })} title={t('Ptz.Right')}><Icon name="chevronRight" size={18} /></button>
        <button {...hold({ panX: -1, tiltY: -1 })} title={t('Ptz.DownLeft')}><Icon name="arrowDownLeft" size={18} /></button>
        <button {...hold({ tiltY: -1 })} title={t('Ptz.Down')}><Icon name="chevronDown" size={18} /></button>
        <button {...hold({ panX: 1, tiltY: -1 })} title={t('Ptz.DownRight')}><Icon name="arrowDownRight" size={18} /></button>
      </div>
      )}

      <div className="ptz-side">
        {showZoom && (
        <div className="ptz-zoom">
          <button {...hold({ zoom: 1 })} title={t('Ptz.ZoomIn')}><Icon name="plus" size={18} /></button>
          <span className="muted">{t('Ptz.Zoom')}</span>
          <button {...hold({ zoom: -1 })} title={t('Ptz.ZoomOut')}><Icon name="minus" size={18} /></button>
        </div>
        )}

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
                <Icon name="x" size={13} />
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
