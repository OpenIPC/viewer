import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { api, type CameraDto } from '../api'
import { useI18n } from '../i18n'
import { useLiveTile } from '../hooks/useLiveTile'

type Size = 1 | 4 | 9

// Collapse the raw useLiveTile status string into a small set of visual states:
// a dot colour + a terse label, plus whether to show the "still connecting"
// spinner (no frame yet).
type Kind = 'live' | 'connecting' | 'error' | 'offline'
function statusKind(status: string): { kind: Kind; label: string } {
  if (status === 'live' || status.includes('transcoded')) return { kind: 'live', label: 'live' }
  if (status === 'connecting' || status.startsWith('transcod')) return { kind: 'connecting', label: 'connecting' }
  if (status === 'ended') return { kind: 'offline', label: 'offline' }
  return { kind: 'error', label: status }
}

// A single live tile. Memoized and keyed by camera id in the grid so that
// changing the layout size (1<->4<->9) NEVER remounts a tile that stays on
// screen — React matches it by key and only moves the DOM node. That is the
// whole app-like point of Phase 21: the <video>/WebSocket/ffmpeg session for a
// surviving camera is not torn down and restarted on a layout switch.
const Tile = memo(function Tile({ camera }: { camera: CameraDto }) {
  const [video, setVideo] = useState<HTMLVideoElement | null>(null)
  const cellRef = useRef<HTMLDivElement>(null)
  const status = useLiveTile(video, camera.id)
  const { kind, label } = statusKind(status)

  const toggleFullscreen = () => {
    const el = cellRef.current
    if (!el) return
    if (document.fullscreenElement) void document.exitFullscreen()
    else void el.requestFullscreen?.()
  }

  return (
    <div className="cell" ref={cellRef} onDoubleClick={toggleFullscreen}>
      <video ref={setVideo} autoPlay muted playsInline />
      {kind === 'connecting' && (
        <div className="loading">
          <span className="spinner" />
        </div>
      )}
      <span className="status">
        <span className={'dot ' + kind} />
        {label}
      </span>
      <span className="label">{camera.name}</span>
      <button className="expand" title="Fullscreen" onClick={toggleFullscreen}>
        ⤢
      </button>
    </div>
  )
})

// A dashed placeholder for the unused slots of a layout (e.g. a 9-grid with only
// 5 cameras), so the arrangement stays a regular NxN.
function EmptyCell() {
  return (
    <div className="cell empty-slot">
      <div className="empty">—</div>
    </div>
  )
}

export function Grid() {
  const { t } = useI18n()
  const [params, setParams] = useSearchParams()
  const [cameras, setCameras] = useState<CameraDto[] | null>(null)
  const [size, setSize] = useState<Size>(4)

  const only = params.get('only')

  useEffect(() => {
    let cancelled = false
    void api.cameras().then((c) => {
      if (!cancelled) setCameras(c)
    })
    return () => {
      cancelled = true
    }
  }, [])

  // The cameras to show: a single one when arriving from "▶ Live", else the
  // grid-preferred cameras (IncludedInGrid first) truncated to the layout size.
  const shown = useMemo(() => {
    if (!cameras) return []
    if (only) return cameras.filter((c) => c.id === only)
    const ordered = [...cameras].sort((a, b) => {
      if (a.includedInGrid !== b.includedInGrid) return a.includedInGrid ? -1 : 1
      return a.sortOrder - b.sortOrder
    })
    return ordered.slice(0, size)
  }, [cameras, size, only])

  const effectiveSize: Size = only ? 1 : size

  return (
    <div className="wrap" style={{ maxWidth: 'none' }}>
      <div className="toolbar">
        <h1 style={{ margin: 0, flex: 1 }}>{t('Grid.Title')}</h1>
        {only ? (
          <button onClick={() => setParams({}, { replace: true })}>{t('Cameras.Th.Grid')}</button>
        ) : (
          <div className="tabs">
            {([1, 4, 9] as Size[]).map((n) => (
              <button
                key={n}
                className={size === n ? 'active' : ''}
                onClick={() => setSize(n)}
              >
                {n}
              </button>
            ))}
          </div>
        )}
      </div>

      {cameras === null ? (
        <div className="spin">{t('Common.Loading')}</div>
      ) : shown.length === 0 ? (
        <p className="muted">{t('Grid.Empty')}</p>
      ) : (
        <div className={`videos n${effectiveSize}`}>
          {shown.map((c) => (
            <Tile key={c.id} camera={c} />
          ))}
          {/* Pad the layout to a regular grid when there are fewer cameras than slots. */}
          {Array.from({ length: Math.max(0, effectiveSize - shown.length) }, (_, i) => (
            <EmptyCell key={`empty-${i}`} />
          ))}
        </div>
      )}
    </div>
  )
}
