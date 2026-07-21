import { useEffect, useRef } from 'react'
import { setPoolContainer } from './liveSession'

// The parking lot for live <video> elements that no tile is currently showing.
//
// Rendered once at the app root, ABOVE the router, so it is never unmounted by
// navigation. It must stay in the document and stay visible-ish: a media element
// removed from the document is paused by the UA, and some browsers throttle a
// `display:none` video. Hence 1×1 px, clipped and pushed off-screen rather than
// hidden.
export function LivePool() {
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    setPoolContainer(ref.current)
    return () => setPoolContainer(null)
  }, [])

  return <div ref={ref} className="live-pool" aria-hidden="true" />
}
