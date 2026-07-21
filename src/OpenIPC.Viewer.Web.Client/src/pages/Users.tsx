import { useEffect, useState } from 'react'
import { api, ApiError, type CameraDto, type WebUserDto } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'
import { ConfirmModal } from '../components/Modals'

// Web console accounts: who may sign in, what they may do, and which cameras
// they see. Mirrors the desktop access-config model (permission flags + camera
// subset) so the two stay conceptually the same product feature.
const PERMISSIONS = ['ViewLive', 'Ptz', 'Export', 'Manage'] as const

export function Users() {
  const { t } = useI18n()
  const { user } = useAuth()
  const [users, setUsers] = useState<WebUserDto[] | null>(null)
  const [available, setAvailable] = useState(true)
  const [cameras, setCameras] = useState<CameraDto[]>([])
  const [editing, setEditing] = useState<WebUserDto | 'new' | null>(null)
  const [deleting, setDeleting] = useState<WebUserDto | null>(null)

  const load = async () => {
    const [roster, cams] = await Promise.all([api.users(), api.cameras()])
    setUsers(roster.users)
    setAvailable(roster.available)
    setCameras(cams)
  }

  useEffect(() => {
    void load()
  }, [])

  if (users === null) return <div className="spin">{t('Common.Loading')}</div>

  return (
    <div className="wrap" style={{ maxWidth: 900 }}>
      <div className="toolbar">
        <h1 style={{ margin: 0, flex: 1 }}>{t('Users.Title')}</h1>
        <button className="primary" disabled={!available} onClick={() => setEditing('new')}>
          {t('Users.Add')}
        </button>
      </div>

      <p className="muted" style={{ fontSize: 12, marginTop: -8 }}>
        {t('Users.BootstrapNote').replace('{user}', user?.user ?? 'admin')}
      </p>
      {!available && <p className="err">{t('Users.Unavailable')}</p>}

      {users.length === 0 ? (
        <p className="muted">{t('Users.Empty')}</p>
      ) : (
        <table className="list">
          <thead>
            <tr>
              <th>{t('Users.Th.Name')}</th>
              <th>{t('Users.Th.Permissions')}</th>
              <th>{t('Users.Th.Cameras')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {users.map((u) => (
              <tr key={u.name}>
                <td>{u.name}</td>
                <td>
                  <span className="row" style={{ flexWrap: 'wrap', gap: 6 }}>
                    {u.permissions.map((p) => (
                      <span key={p} className={'badge' + (p === 'Manage' ? ' on' : '')}>
                        {t('Users.Perm.' + p)}
                      </span>
                    ))}
                  </span>
                </td>
                <td className="muted">
                  {u.cameras === null ? t('Users.AllCameras') : `${u.cameras.length}`}
                </td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <button onClick={() => setEditing(u)}>{t('Cameras.Edit')}</button>{' '}
                  <button className="danger" onClick={() => setDeleting(u)}>
                    {t('Cameras.Delete')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {editing && (
        <UserEditor
          user={editing === 'new' ? null : editing}
          cameras={cameras}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null)
            await load()
          }}
        />
      )}
      {deleting && (
        <ConfirmModal
          title={t('Users.DeleteConfirm')}
          message={deleting.name}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={async () => {
            const name = deleting.name
            setDeleting(null)
            await api.deleteUser(name)
            await load()
          }}
          onCancel={() => setDeleting(null)}
        />
      )}
    </div>
  )
}

function UserEditor({
  user, cameras, onClose, onSaved,
}: {
  user: WebUserDto | null
  cameras: CameraDto[]
  onClose: () => void
  onSaved: () => void
}) {
  const { t } = useI18n()
  const [name, setName] = useState(user?.name ?? '')
  const [password, setPassword] = useState('')
  const [permissions, setPermissions] = useState<string[]>(user?.permissions ?? ['ViewLive'])
  const [allCameras, setAllCameras] = useState(user ? user.cameras === null : true)
  const [subset, setSubset] = useState<string[]>(user?.cameras ?? [])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const toggle = (list: string[], value: string) =>
    list.includes(value) ? list.filter((x) => x !== value) : [...list, value]

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      if (user) {
        await api.updateUser(user.name, {
          password: password || undefined,
          permissions,
          cameras: allCameras ? undefined : subset,
          allCameras,
        })
      } else {
        await api.createUser({
          name: name.trim(),
          password,
          permissions,
          cameras: allCameras ? null : subset,
        })
      }
      onSaved()
    } catch (e) {
      setError(
        e instanceof ApiError && e.code === 'user_exists'
          ? t('Users.Exists')
          : e instanceof ApiError && e.details?.length
            ? e.details.join(', ')
            : t('Users.SaveFailed'),
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h2>{user ? t('Users.EditTitle') : t('Users.AddTitle')}</h2>
        <div className="form-grid">
          <div className="two">
            <label>
              {t('Users.Th.Name')}
              <input value={name} disabled={!!user} onChange={(e) => setName(e.target.value)} />
            </label>
            <label>
              {t('Editor.Password')}
              <input
                type="password"
                value={password}
                placeholder={user ? t('Editor.KeepBlank') : ''}
                onChange={(e) => setPassword(e.target.value)}
              />
            </label>
          </div>

          <div>
            <span className="muted" style={{ fontSize: 12 }}>{t('Users.Th.Permissions')}</span>
            <div className="row" style={{ flexWrap: 'wrap', gap: 12, marginTop: 6 }}>
              {PERMISSIONS.map((p) => (
                <label key={p} className="row" style={{ flexDirection: 'row', gap: 6 }}>
                  <input
                    type="checkbox"
                    checked={permissions.includes(p)}
                    onChange={() => setPermissions((prev) => toggle(prev, p))}
                  />
                  {t('Users.Perm.' + p)}
                </label>
              ))}
            </div>
          </div>

          <div>
            <label className="row" style={{ flexDirection: 'row', gap: 6 }}>
              <input type="checkbox" checked={allCameras} onChange={(e) => setAllCameras(e.target.checked)} />
              {t('Users.AllCameras')}
            </label>
            {!allCameras && (
              <div className="camera-picker">
                {cameras.map((c) => (
                  <label key={c.id} className="row" style={{ flexDirection: 'row', gap: 6 }}>
                    <input
                      type="checkbox"
                      checked={subset.includes(c.id)}
                      onChange={() => setSubset((prev) => toggle(prev, c.id))}
                    />
                    {c.name}
                  </label>
                ))}
              </div>
            )}
          </div>

          {error && <p className="err" style={{ margin: 0 }}>{error}</p>}
        </div>

        <div className="modal-actions">
          <button onClick={onClose}>{t('Common.Cancel')}</button>
          <button
            className="primary"
            disabled={busy || (!user && (!name.trim() || !password)) || permissions.length === 0}
            onClick={() => void save()}
          >
            {t('Common.Save')}
          </button>
        </div>
      </div>
    </div>
  )
}
