import { useState, type FormEvent } from 'react'
import { ApiError } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'

export function Login() {
  const { login } = useAuth()
  const { t } = useI18n()
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState(false)
  const [busy, setBusy] = useState(false)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError(false)
    setBusy(true)
    try {
      await login(user, password)
      // No navigate() needed: AuthProvider now has a user, so the router swaps
      // /login for the app automatically.
    } catch (err) {
      setError(err instanceof ApiError ? err.status !== 429 : true)
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <h1 style={{ textAlign: 'center' }}>OpenIPC Viewer</h1>
      <div className="card">
        <p className="muted" style={{ marginTop: 0 }}>
          {t('Login.Subtitle')}
        </p>
        <form onSubmit={onSubmit} className="form-grid">
          <label>
            {t('Login.Username')}
            <input value={user} onChange={(e) => setUser(e.target.value)} autoFocus autoComplete="username" />
          </label>
          <label>
            {t('Login.Password')}
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
            />
          </label>
          {error && <div className="err">{t('Login.Error')}</div>}
          <button type="submit" className="primary" disabled={busy}>
            {t('Login.SignIn')}
          </button>
        </form>
      </div>
    </div>
  )
}
