import { memo, useEffect, useState } from 'react'
import { api, type CameraDto } from '../api'
import { useI18n } from '../i18n'

// A tile that shows a periodically refreshed still instead of a live stream —
// the web counterpart of the desktop grid's "stills" mode.
//
// The point is cost: a 25-cell wall means 25 ffmpeg sessions and 25 sockets,
// while a still costs one HTTP GET per camera per interval and the camera
// itself encodes the JPEG (Majestic /image.jpg). For a wall you glance at,
// a frame every few seconds is what you actually need.
//
// Fetched as a blob rather than pointed at with <img src>, for the same reason
// as SnapshotModal: the endpoint answers 502 when the camera doesn't respond,
// and an <img> would only show a broken icon. It also means a failed refresh
// leaves the previous frame on screen instead of blanking the tile.
export const StillTile = memo(function StillTile({ camera, intervalSeconds }: {
  camera: CameraDto
  intervalSeconds: number
}) {
  const { t } = useI18n()
  const [url, setUrl] = useState<string | null>(null)
  const [takenAt, setTakenAt] = useState<Date | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false
    let timer: number | undefined
    // The frame on screen (`shown`) is never revoked while it's displayed: the
    // one released at the top of a tick is the generation before it, which the
    // browser stopped referencing an interval ago.
    let shown: string | null = null
    let stale: string | null = null

    const tick = async () => {
      if (stale) {
        URL.revokeObjectURL(stale)
        stale = null
      }
      try {
        const res = await fetch(api.snapshotUrl(camera.id), {
          credentials: 'same-origin',
          cache: 'no-store',
        })
        if (!res.ok) throw new Error(String(res.status))
        const blob = await res.blob()
        if (cancelled) return
        const next = URL.createObjectURL(blob)
        stale = shown
        shown = next
        setUrl(next)
        setTakenAt(new Date())
        setFailed(false)
      } catch {
        if (!cancelled) setFailed(true)
      }
      // Chained rather than setInterval: a camera that takes eight seconds to
      // answer must not have requests queueing up behind it.
      if (!cancelled) timer = window.setTimeout(tick, Math.max(1, intervalSeconds) * 1000)
    }

    void tick()
    return () => {
      cancelled = true
      window.clearTimeout(timer)
      if (shown) URL.revokeObjectURL(shown)
      if (stale) URL.revokeObjectURL(stale)
    }
  }, [camera.id, intervalSeconds])

  return (
    <div className="cell">
      {url ? <img className="still" src={url} alt={camera.name} /> : null}
      {!url && !failed && (
        <div className="loading">
          <span className="spinner" />
        </div>
      )}
      <span className="status">
        <span className={'dot ' + (failed ? 'error' : url ? 'live' : 'connecting')} />
        {failed ? t('Grid.StillFailed') : takenAt ? takenAt.toLocaleTimeString() : t('Snapshot.Taking')}
      </span>
      <span className="label">{camera.name}</span>
    </div>
  )
})
