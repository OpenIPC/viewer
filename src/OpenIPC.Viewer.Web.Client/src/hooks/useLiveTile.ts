import { useEffect, useReducer, useState } from 'react'
import { acquire, release, type LiveSession } from '../live/liveSession'

// Attaches a pooled live session to a tile's slot and exposes it.
//
// The tile owns neither the <video> nor the socket (see live/liveSession.ts):
// it borrows a session for as long as it's mounted and hands it back after.
// So a tile mounting is no longer the same thing as a stream starting —
// switching grid size, flipping pages, or navigating to another route and back
// re-attaches a session that never stopped playing.
//
// The session is returned rather than just its status because it also carries
// what the tile's controls need (whether the stream has sound, whether it's
// muted). Every change re-renders through the same subscription.
export function useLiveTile(slot: HTMLElement | null, cameraId: string): {
  status: string
  session: LiveSession | null
} {
  const [session, setSession] = useState<LiveSession | null>(null)
  const [, changed] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (!slot) return
    const acquired = acquire(cameraId)
    slot.appendChild(acquired.video)
    setSession(acquired)
    const unsubscribe = acquired.subscribe(changed)
    return () => {
      unsubscribe()
      setSession(null)
      release(acquired)
    }
  }, [slot, cameraId])

  return { status: session?.status ?? 'connecting', session }
}
