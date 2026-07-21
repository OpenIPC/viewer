import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, type GroupDto } from '../api'
import { useI18n } from '../i18n'

// Camera-group CRUD against /api/v1/groups. Deleting a group cameras still use
// returns 409 group_in_use — surfaced inline rather than swallowed.
export function Groups() {
  const { t } = useI18n()
  const [groups, setGroups] = useState<GroupDto[] | null>(null)
  const [adding, setAdding] = useState('')
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setGroups(await api.groups())
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const onAdd = async () => {
    const name = adding.trim()
    if (!name) return
    await api.createGroup(name)
    setAdding('')
    await load()
  }

  const onRename = async (g: GroupDto) => {
    const name = prompt(t('Groups.Name'), g.name)?.trim()
    if (!name || name === g.name) return
    await api.renameGroup(g.id, name)
    await load()
  }

  const onDelete = async (g: GroupDto) => {
    if (!confirm(t('Groups.DeleteConfirm'))) return
    setError(null)
    try {
      await api.deleteGroup(g.id)
      await load()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) setError(t('Groups.InUse'))
      else throw err
    }
  }

  return (
    <div className="wrap" style={{ maxWidth: 640 }}>
      <h1>{t('Groups.Title')}</h1>

      <div className="toolbar">
        <input
          value={adding}
          onChange={(e) => setAdding(e.target.value)}
          placeholder={t('Groups.NewName')}
          onKeyDown={(e) => e.key === 'Enter' && onAdd()}
          style={{ flex: 1 }}
        />
        <button className="primary" onClick={onAdd}>
          {t('Groups.Add')}
        </button>
      </div>

      {error && <p className="err">{error}</p>}

      {groups === null ? (
        <div className="spin">{t('Common.Loading')}</div>
      ) : groups.length === 0 ? (
        <p className="muted">{t('Groups.Empty')}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t('Groups.Name')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {groups.map((g) => (
              <tr key={g.id}>
                <td>{g.name}</td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <button onClick={() => onRename(g)}>{t('Cameras.Edit')}</button>{' '}
                  <button className="danger" onClick={() => onDelete(g)}>
                    {t('Cameras.Delete')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
