import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'
import { ConfirmModal } from '../components/Modals'

type Status = { version: string; cameras: number; groups: number; sessions: number; streams: number }

// System status + backup/restore + session revocation, all over /api/v1 JSON.
// Import is a same-origin multipart POST; revoke-all clears the cookie
// server-side and we then drop local auth state.
export function System() {
  const { t, setLang } = useI18n()
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const [status, setStatus] = useState<Status | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)
  const [confirmRevoke, setConfirmRevoke] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  const loadStatus = async () => setStatus(await api.system())
  useEffect(() => {
    void loadStatus()
  }, [])

  const onImport = async (file: File) => {
    setNotice(null)
    try {
      const { added, updated } = await api.importConfig(file)
      setNotice({ ok: true, text: `${t('System.Imported')} +${added} / ~${updated}` })
      await loadStatus()
    } catch {
      setNotice({ ok: false, text: t('System.ImportError') })
    }
    if (fileRef.current) fileRef.current.value = ''
  }

  const onRevokeAll = async () => {
    setConfirmRevoke(false)
    await api.revokeAllSessions()
    await logout() // our own cookie is gone server-side; clear local state + go to login
    navigate('/login', { replace: true })
  }

  return (
    <div className="wrap" style={{ maxWidth: 720 }}>
      <h1>{t('System.Title')}</h1>

      {notice && (
        <p style={{ color: notice.ok ? 'var(--success)' : 'var(--danger)' }}>{notice.text}</p>
      )}

      <table className="details" style={{ maxWidth: 420, marginBottom: 28 }}>
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

      {/* Account controls also live here because the mobile bottom-tab nav has no
          room for the sidebar footer (user / language / sign out). */}
      <h2 style={{ fontSize: 16, fontWeight: 600 }}>{t('System.Account')}</h2>
      <div className="row" style={{ flexWrap: 'wrap', gap: 12, margin: '12px 0 28px' }}>
        <span className="muted">{user?.user}</span>
        <span>
          <a onClick={() => setLang('en')} style={{ cursor: 'pointer' }}>
            EN
          </a>{' '}
          ·{' '}
          <a onClick={() => setLang('ru')} style={{ cursor: 'pointer' }}>
            RU
          </a>
        </span>
        <button
          onClick={async () => {
            await logout()
            navigate('/login', { replace: true })
          }}
        >
          {t('Nav.SignOut')}
        </button>
      </div>

      <h2 style={{ fontSize: 16, fontWeight: 600 }}>{t('System.Backup')}</h2>
      <div style={{ display: 'flex', gap: '1.5rem', alignItems: 'center', flexWrap: 'wrap', margin: '12px 0' }}>
        <a
          href={api.configExportUrl}
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
        onClick={() => setConfirmRevoke(true)}
        style={{ marginTop: 8, borderColor: '#5a2a2a', color: 'var(--danger)' }}
      >
        {t('System.RevokeAll')}
      </button>

      {confirmRevoke && (
        <ConfirmModal
          title={t('System.RevokeAll')}
          message={t('System.RevokeConfirm')}
          confirmLabel={t('System.RevokeAll')}
          danger
          onConfirm={onRevokeAll}
          onCancel={() => setConfirmRevoke(false)}
        />
      )}
    </div>
  )
}
