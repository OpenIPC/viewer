import { useCallback, useEffect, useRef, useState } from 'react'

const MAX_ZOOM = 8
const STEP = 1.25

// Digital zoom for a live tile: scale the picture up and drag the visible
// window around it. Purely client-side — the stream is untouched, so zooming
// costs nothing on the camera or the server and never restarts anything.
//
// The transform goes on the tile's own slot element, NOT on the <video>: the
// video is pooled and moves between tiles (see live/liveSession.ts), so a
// transform left on it would follow the camera into the next cell it lands in.
export function useDigitalZoom(cell: HTMLElement | null) {
  const [zoom, setZoom] = useState(1)
  const [pan, setPan] = useState({ x: 0, y: 0 })
  // Mirrors of the state, written before setState rather than during render:
  // several wheel notches (or pointer moves) can land inside one frame, and if
  // each read the last *rendered* zoom they would all compute the same step and
  // only one would count.
  const panRef = useRef(pan)
  const zoomRef = useRef(zoom)

  const applyZoom = useCallback((z: number) => {
    zoomRef.current = z
    setZoom(z)
  }, [])
  const applyPan = useCallback((p: { x: number; y: number }) => {
    panRef.current = p
    setPan(p)
  }, [])

  // At zoom z the picture is z times the box, so the window can travel at most
  // half the overhang in each direction before showing empty space.
  const clamp = useCallback((x: number, y: number, z: number) => {
    const rect = cell?.getBoundingClientRect()
    const maxX = rect ? (rect.width * (z - 1)) / 2 : 0
    const maxY = rect ? (rect.height * (z - 1)) / 2 : 0
    return {
      x: Math.max(-maxX, Math.min(maxX, x)),
      y: Math.max(-maxY, Math.min(maxY, y)),
    }
  }, [cell])

  const setZoomTo = useCallback((z: number) => {
    const next = Math.max(1, Math.min(MAX_ZOOM, z))
    applyZoom(next)
    const p = panRef.current
    applyPan(next === 1 ? { x: 0, y: 0 } : clamp(p.x, p.y, next))
  }, [applyPan, applyZoom, clamp])

  const zoomIn = useCallback(() => setZoomTo(zoomRef.current * STEP), [setZoomTo])
  const zoomOut = useCallback(() => setZoomTo(zoomRef.current / STEP), [setZoomTo])
  const reset = useCallback(() => {
    applyZoom(1)
    applyPan({ x: 0, y: 0 })
  }, [applyPan, applyZoom])

  // Wheel is bound natively rather than through React's onWheel: React's root
  // listener is passive, so preventDefault there can't stop the page scrolling
  // under the cursor while you zoom.
  useEffect(() => {
    if (!cell) return
    const onWheel = (e: WheelEvent) => {
      e.preventDefault()
      setZoomTo(zoomRef.current * (e.deltaY < 0 ? STEP : 1 / STEP))
    }
    cell.addEventListener('wheel', onWheel, { passive: false })
    return () => cell.removeEventListener('wheel', onWheel)
  }, [cell, setZoomTo])

  // Drag to pan, once zoomed in. Pointer capture so the drag survives the
  // cursor leaving the tile, and buttons stay clickable.
  useEffect(() => {
    if (!cell) return
    let dragging = false
    let originX = 0
    let originY = 0
    let startPan = { x: 0, y: 0 }

    const onDown = (e: PointerEvent) => {
      if (zoomRef.current === 1 || e.button !== 0) return
      if ((e.target as HTMLElement).closest('button')) return
      dragging = true
      originX = e.clientX
      originY = e.clientY
      startPan = panRef.current
      cell.setPointerCapture(e.pointerId)
    }
    const onMove = (e: PointerEvent) => {
      if (!dragging) return
      e.preventDefault()
      applyPan(clamp(startPan.x + (e.clientX - originX), startPan.y + (e.clientY - originY), zoomRef.current))
    }
    const onUp = (e: PointerEvent) => {
      if (!dragging) return
      dragging = false
      try {
        cell.releasePointerCapture(e.pointerId)
      } catch { /* pointer already gone */ }
    }

    cell.addEventListener('pointerdown', onDown)
    cell.addEventListener('pointermove', onMove)
    cell.addEventListener('pointerup', onUp)
    cell.addEventListener('pointercancel', onUp)
    return () => {
      cell.removeEventListener('pointerdown', onDown)
      cell.removeEventListener('pointermove', onMove)
      cell.removeEventListener('pointerup', onUp)
      cell.removeEventListener('pointercancel', onUp)
    }
  }, [cell, clamp, applyPan])

  return {
    zoom,
    zoomed: zoom > 1,
    transform: zoom === 1 ? undefined : `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
    zoomIn,
    zoomOut,
    reset,
  }
}
