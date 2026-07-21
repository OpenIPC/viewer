import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, ApiError } from './api'

// Session state driven entirely by the HttpOnly cookie: we never see the token.
// On mount we probe /api/v1/auth/me — a 401 means "show login". login()/logout()
// flip state in place, so the whole app never reloads the page.
type User = { user: string; roles: string[] }
type Auth = {
  user: User | null
  loading: boolean
  login: (user: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const Ctx = createContext<Auth | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    api
      .me()
      .then((me) => {
        if (!cancelled) setUser({ user: me.user, roles: me.roles })
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
    setUser({ user: res.user, roles: res.roles })
  }, [])

  const logout = useCallback(async () => {
    try {
      await api.logout()
    } finally {
      setUser(null)
    }
  }, [])

  return <Ctx.Provider value={{ user, loading, login, logout }}>{children}</Ctx.Provider>
}

export function useAuth(): Auth {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error('useAuth outside AuthProvider')
  return ctx
}
