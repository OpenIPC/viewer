import { memo, useState } from 'react'
import type { CameraDto } from '../api'
import { useDigitalZoom } from '../hooks/useDigitalZoom'
import { useLiveTile } from '../hooks/useLiveTile'
import { useI18n } from '../i18n'
import { Icon } from './Icon'
import { SnapshotModal } from './SnapshotModal'

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
// screen — React matches it by key and only moves the DOM node.
//
// The <video> itself is not rendered here: the tile provides an empty slot and
// borrows a pooled element from live/liveSession.ts, so even an actual unmount
// (route change, page flip) leaves the stream running for a grace period.
export const LiveTile = memo(function LiveTile({ camera }: { camera: CameraDto }) {
  const { t } = useI18n()
  const [slot, setSlot] = useState<HTMLDivElement | null>(null)
  const [snapshot, setSnapshot] = useState(false)
  const [cell, setCell] = useState<HTMLDivElement | null>(null)
  const { status, session } = useLiveTile(slot, camera.id)
  const { kind, label } = statusKind(status)
  const zoom = useDigitalZoom(cell)

  const toggleFullscreen = () => {
    if (!cell) return
    if (document.fullscreenElement) void document.exitFullscreen()
    else void cell.requestFullscreen?.()
  }

  return (
    <div
      className={'cell' + (zoom.zoomed ? ' zoomed' : '')}
      ref={setCell}
      onDoubleClick={toggleFullscreen}
    >
      <div className="video-slot" ref={setSlot} style={{ transform: zoom.transform }} />
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
      {/* Zoom controls: the wheel does this too, but a wall is often driven by
          touch, where there is no wheel. Shown zoomed-in regardless of hover so
          the way out is always visible. */}
      <div className="zoom-pad">
        {zoom.zoomed && (
          <button title={t('Tile.ZoomReset')} onClick={zoom.reset}>
            <span className="zoom-level">{zoom.zoom.toFixed(1)}×</span>
          </button>
        )}
        <button title={t('Tile.ZoomOut')} onClick={zoom.zoomOut} disabled={!zoom.zoomed}>
          <Icon name="minus" size={15} />
        </button>
        <button title={t('Tile.ZoomIn')} onClick={zoom.zoomIn}>
          <Icon name="plus" size={15} />
        </button>
      </div>
      {session?.hasAudio && (
        <button
          className="listen"
          title={session.muted ? t('Tile.Listen') : t('Tile.Mute')}
          onClick={() => session.setMuted(!session.muted)}
        >
          <Icon name={session.muted ? 'volumeOff' : 'volumeOn'} size={15} />
        </button>
      )}
      <button className="snap" title={t('Snapshot.Take')} onClick={() => setSnapshot(true)}>
        <Icon name="camera" size={15} />
      </button>
      <button className="expand" title={t('Tile.Fullscreen')} onClick={toggleFullscreen}>
        <Icon name="maximize" size={15} />
      </button>
      {snapshot && (
        <SnapshotModal cameraId={camera.id} cameraName={camera.name} onClose={() => setSnapshot(false)} />
      )}
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
