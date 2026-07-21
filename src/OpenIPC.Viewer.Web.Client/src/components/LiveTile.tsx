import { memo, useRef, useState } from 'react'
import type { CameraDto } from '../api'
import { useLiveTile } from '../hooks/useLiveTile'

// Collapse the raw useLiveTile status string into a small set of visual states:
// a dot colour + a terse label, plus whether to show the "still connecting"
// spinner (no frame yet).
type Kind = 'live' | 'connecting' | 'error' | 'offline'

export function statusKind(status: string): { kind: Kind; label: string } {
  if (status === 'live' || status.includes('transcoded')) return { kind: 'live', label: 'live' }
  if (status === 'connecting' || status.startsWith('transcod')) return { kind: 'connecting', label: 'connecting' }
  if (status === 'ended') return { kind: 'offline', label: 'offline' }
  return { kind: 'error', label: status }
}

// A single live tile, shared by the grid and the single-camera page. Memoized and
// keyed by camera id where it's used in a list, so changing the layout (size OR
// which saved layout is active) NEVER remounts a tile whose camera stays on
// screen — React matches it by key and only moves the DOM node. That is the
// app-like point: the <video>/WebSocket/ffmpeg session for a surviving camera is
// not torn down and restarted on a switch.
export const LiveTile = memo(function LiveTile({ camera }: { camera: CameraDto }) {
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
export function EmptyCell() {
  return (
    <div className="cell empty-slot">
      <div className="empty">—</div>
    </div>
  )
}
