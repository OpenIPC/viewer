import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, type CameraDto, type GroupDto } from '../api'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'
import { useAuth } from '../auth'
import { CameraEditor } from '../components/CameraEditor'
import { ConfirmModal } from '../components/Modals'

// Camera list + CRUD. Everything mutates in place — add/edit/delete refetch the
// list and re-render without a page reload (the app-like win over Razor's
// full-page form posts).
export function Cameras() {
  const { t } = useI18n()
  const { can } = useAuth()
  const [cameras, setCameras] = useState<CameraDto[] | null>(null)
  const [groups, setGroups] = useState<GroupDto[]>([])
  const [editing, setEditing] = useState<CameraDto | null | undefined>(undefined) // undefined=closed, null=add
  const [deleting, setDeleting] = useState<CameraDto | null>(null)

  const load = useCallback(async () => {
    const [cams, grps] = await Promise.all([api.cameras(), api.groups()])
    setCameras(cams)
    setGroups(grps)
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const onDelete = async () => {
    if (!deleting) return
    const id = deleting.id
    setDeleting(null)
    await api.deleteCamera(id)
    await load()
  }

  const onSaved = async () => {
    setEditing(undefined)
    await load()
  }

  return (
    <div className="wrap">
      <div className="toolbar">
        <h1 style={{ margin: 0, flex: 1 }}>{t('Cameras.Title')}</h1>
        {can('Manage') && (
          <>
            {/* Finding cameras on the network is a way to add one, so it sits
                next to Add rather than in its own nav entry. */}
            <Link to="/discovery" className="row button-link ghost">
              <Icon name="search" /> {t('Nav.Discovery')}
            </Link>
            <button className="primary row" onClick={() => setEditing(null)}>
              <Icon name="plus" /> {t('Cameras.Add')}
            </button>
          </>
        )}
      </div>

      {cameras === null ? (
        <div className="spin">{t('Common.Loading')}</div>
      ) : cameras.length === 0 ? (
        <p className="muted">{t('Cameras.Empty')}</p>
      ) : (
        <table className="list">
          <thead>
            <tr>
              <th>{t('Cameras.Th.Name')}</th>
              <th>{t('Cameras.Th.Host')}</th>
              <th>{t('Cameras.Th.Group')}</th>
              <th>{t('Cameras.Th.Grid')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {cameras.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td>
                <td className="mono muted">{c.host}</td>
                <td className="muted">{c.groupName ?? '—'}</td>
                <td>
                  <span className={'badge' + (c.includedInGrid ? ' on' : '')}>
                    {c.includedInGrid ? t('Common.Yes') : t('Common.No')}
                  </span>
                </td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <Link to={`/camera/${c.id}`} className="row"><Icon name="play" size={13} /> {t('Cameras.Live')}</Link>{' '}
                  {can('Manage') && (
                    <>
                      <button onClick={() => setEditing(c)}>{t('Cameras.Edit')}</button>{' '}
                      <button className="danger" onClick={() => setDeleting(c)}>
                        {t('Cameras.Delete')}
                      </button>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {editing !== undefined && (
        <CameraEditor
          camera={editing}
          groups={groups}
          onClose={() => setEditing(undefined)}
          onSaved={onSaved}
        />
      )}

      {deleting && (
        <ConfirmModal
          title={t('Cameras.DeleteConfirm')}
          message={deleting.name}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={onDelete}
          onCancel={() => setDeleting(null)}
        />
      )}
    </div>
  )
}
