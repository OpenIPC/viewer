import { useEffect, useState } from 'react'
import { acquire, release } from '../live/liveSession'

// Attaches a pooled live session to a tile's slot and reports its status.
//
// The tile owns neither the <video> nor the socket (see live/liveSession.ts):
// it borrows a session for as long as it's mounted and hands it back after.
// So a tile mounting is no longer the same thing as a stream starting —
// switching grid size, flipping pages, or navigating to another route and back
// re-attaches a session that never stopped playing.
export function useLiveTile(slot: HTMLElement | null, cameraId: string): string {
  const [status, setStatus] = useState('connecting')

  useEffect(() => {
    if (!slot) return
    const session = acquire(cameraId)
    slot.appendChild(session.video)
    setStatus(session.status)
    const unsubscribe = session.subscribe(setStatus)
    return () => {
      unsubscribe()
      release(session)
    }
  }, [slot, cameraId])

  return status
}
