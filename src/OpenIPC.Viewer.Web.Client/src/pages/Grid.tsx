import { memo, useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { api, type CameraDto } from '../api'
import { useI18n } from '../i18n'
import { useLiveTile } from '../hooks/useLiveTile'

type Size = 1 | 4 | 9

// A single live tile. Memoized and keyed by camera id in the grid so that
// changing the layout size (1<->4<->9) NEVER remounts a tile that stays on
// screen — React matches it by key and only moves the DOM node. That is the
// whole app-like point of Phase 21: the <video>/WebSocket/ffmpeg session for a
// surviving camera is not torn down and restarted on a layout switch.
const Tile = memo(function Tile({ camera }: { camera: CameraDto }) {
  const [video, setVideo] = useState<HTMLVideoElement | null>(null)
  const status = useLiveTile(video, camera.id)
  return (
    <div className="cell">
      <video ref={setVideo} autoPlay muted playsInline />
      <span className="label">{camera.name}</span>
      <span className="status">{status}</span>
    </div>
  )
})

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
    <div className="wrap">
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
        </div>
      )}
    </div>
  )
}
