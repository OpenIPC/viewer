import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'

type Status = { version: string; cameras: number; groups: number; sessions: number; streams: number }

// System status + backup/restore + session revocation. Status is JSON
// (/api/v1/system). Export/import/revoke reuse the Phase 20 /app form endpoints
// (they already exist and are Origin-guarded); import is a same-origin multipart
// fetch, revoke clears the cookie server-side and we then drop local auth state.
export function System() {
  const { t } = useI18n()
  const { logout } = useAuth()
  const navigate = useNavigate()
  const [status, setStatus] = useState<Status | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const loadStatus = async () => setStatus(await api.system())
  useEffect(() => {
    void loadStatus()
  }, [])

  const onImport = async (file: File) => {
    setNotice(null)
    const form = new FormData()
    form.append('file', file)
    const res = await fetch('/app/config/import', {
      method: 'POST',
      body: form,
      credentials: 'same-origin',
    })
    // The endpoint redirects to /app/system?added=..&updated=.. (or ?error=1).
    const url = new URL(res.url, location.origin)
    if (!res.ok || url.searchParams.has('error')) {
      setNotice({ ok: false, text: t('System.ImportError') })
    } else {
      const added = url.searchParams.get('added') ?? '0'
      const updated = url.searchParams.get('updated') ?? '0'
      setNotice({ ok: true, text: `${t('System.Imported')} +${added} / ~${updated}` })
      await loadStatus()
    }
    if (fileRef.current) fileRef.current.value = ''
  }

  const onRevokeAll = async () => {
    if (!confirm(t('System.RevokeConfirm'))) return
    await fetch('/app/sessions/revoke-all', { method: 'POST', credentials: 'same-origin' })
    await logout() // our own cookie is gone server-side; clear local state + go to login
    navigate('/login', { replace: true })
  }

  return (
    <div className="wrap" style={{ maxWidth: 720 }}>
      <h1>{t('System.Title')}</h1>

      {notice && (
        <p style={{ color: notice.ok ? 'var(--success)' : 'var(--danger)' }}>{notice.text}</p>
      )}

      <table style={{ maxWidth: 420, marginBottom: 28 }}>
        <tbody>
          <tr>
            <td className="muted">{t('System.Version')}</td>
            <td className="mono">{status?.version ?? '…'}</td>
          </tr>
          <tr>
            <td className="muted">{t('System.Cameras')}</td>
            <td>{status?.cameras ?? '…'}</td>
          </tr>
          <tr>
            <td className="muted">{t('System.Groups')}</td>
            <td>{status?.groups ?? '…'}</td>
          </tr>
          <tr>
            <td className="muted">{t('System.Sessions')}</td>
            <td>{status?.sessions ?? '…'}</td>
          </tr>
          <tr>
            <td className="muted">{t('System.Streams')}</td>
            <td>{status?.streams ?? '…'}</td>
          </tr>
        </tbody>
      </table>

      <h2 style={{ fontSize: 16, fontWeight: 600 }}>{t('System.Backup')}</h2>
      <div style={{ display: 'flex', gap: '1.5rem', alignItems: 'center', flexWrap: 'wrap', margin: '12px 0' }}>
        <a
          href="/app/config/export"
          style={{
            display: 'inline-block',
            padding: '8px 12px',
            border: '1px solid var(--border-med)',
            borderRadius: 'var(--r-sm)',
            color: 'var(--text-1)',
          }}
        >
          {t('System.Export')}
        </a>
        <div className="row">
          <input
            ref={fileRef}
            type="file"
            accept="application/json,.json"
            onChange={(e) => e.target.files?.[0] && onImport(e.target.files[0])}
          />
        </div>
      </div>
      <p className="muted" style={{ fontSize: 12 }}>
        {t('System.BackupNote')}
      </p>

      <h2 style={{ fontSize: 16, fontWeight: 600, marginTop: 28 }}>{t('System.SessionsTitle')}</h2>
      <button
        onClick={onRevokeAll}
        style={{ marginTop: 8, borderColor: '#5a2a2a', color: 'var(--danger)' }}
      >
        {t('System.RevokeAll')}
      </button>
    </div>
  )
}
