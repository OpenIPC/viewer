import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, ApiError } from './api'

// Session state driven entirely by the HttpOnly cookie: we never see the token.
// On mount we probe /api/v1/auth/me — a 401 means "show login". login()/logout()
// flip state in place, so the whole app never reloads the page.
// `permissions` is what the UI hides things by; the server enforces the same
// set independently, so a hidden button is a courtesy, not the boundary.
// `cameras` null means "all of them" — a restriction is always an explicit list.
type User = { user: string; roles: string[]; permissions: string[]; cameras: string[] | null }
type Auth = {
  user: User | null
  loading: boolean
  can: (permission: Permission) => boolean
  login: (user: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

export type Permission = 'ViewLive' | 'ViewArchive' | 'Ptz' | 'Export' | 'Manage' | 'Talk'

const Ctx = createContext<Auth | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    api
      .me()
      .then((me) => {
        if (!cancelled) setUser(toUser(me))
      })
      .catch((e) => {
        if (!cancelled && !(e instanceof ApiError && e.status === 401)) console.error(e)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [])

  const login = useCallback(async (u: string, p: string) => {
    const res = await api.login(u, p)
    setUser(toUser(res))
  }, [])

  const logout = useCallback(async () => {
    try {
      await api.logout()
    } finally {
      setUser(null)
    }
  }, [])

  const can = useCallback(
    (permission: Permission) => !!user?.permissions.includes(permission),
    [user],
  )

  return <Ctx.Provider value={{ user, loading, can, login, logout }}>{children}</Ctx.Provider>
}

// Tolerates a server that predates permissions (or a provider that omits them):
// no list means no rights, which fails closed in the UI.
function toUser(me: { user: string; roles: string[]; permissions?: string[]; cameras?: string[] | null }): User {
  return { user: me.user, roles: me.roles, permissions: me.permissions ?? [], cameras: me.cameras ?? null }
}

export function useAuth(): Auth {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error('useAuth outside AuthProvider')
  return ctx
}
