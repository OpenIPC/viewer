import { useEffect, useState } from 'react'
import { api, type CameraDto, type SnapshotDto } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'
import { ConfirmModal } from '../components/Modals'
import { ArchiveTabs } from '../components/ArchiveTabs'

const PAGE_SIZE = 60

// The snapshot library: stills kept on the server, in the same folder and the
// same table the desktop head writes — so a still taken from a phone shows up in
// the desktop browser, and the desktop's show up here.
//
// Thumbnails are requested for the grid and the full image only when one is
// opened: a page of sixty 1080p JPEGs is several tens of megabytes, and on a
// phone over Wi-Fi that is the difference between a gallery and a stall.
export function Snapshots() {
  const { t } = useI18n()
  const { can } = useAuth()
  const [items, setItems] = useState<SnapshotDto[] | null>(null)
  const [cameras, setCameras] = useState<CameraDto[]>([])
  const [cameraId, setCameraId] = useState('')
  const [page, setPage] = useState(0)
  const [total, setTotal] = useState(0)
  const [open, setOpen] = useState<SnapshotDto | null>(null)
  const [deleting, setDeleting] = useState<SnapshotDto | null>(null)

  const load = async (pageIndex = page) => {
    const [result, cams] = await Promise.all([
      api.snapshots({
        cameraId: cameraId || undefined,
        offset: pageIndex * PAGE_SIZE,
        limit: PAGE_SIZE,
      }),
      api.cameras(),
    ])
    setItems(result.items)
    setTotal(result.total)
    setCameras(cams)
  }

  useEffect(() => {
    setPage(0)
    void load(0)
    // Changing the camera filter restarts at the first page; paging keeps it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameraId])

  const remove = async () => {
    if (!deleting) return
    await api.deleteSnapshot(deleting.id)
    setDeleting(null)
    if (open?.id === deleting.id) setOpen(null)
    await load()
  }

  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const flip = async (next: number) => {
    setPage(next)
    await load(next)
  }

  return (
    <div className="wrap" style={{ maxWidth: 1000 }}>
      <h1>{t('Nav.Recordings')}</h1>
      <ArchiveTabs />

      <div className="toolbar">
        <span className="muted" style={{ flex: 1 }}>
          {total > 0 && `${page * PAGE_SIZE + 1}–${Math.min((page + 1) * PAGE_SIZE, total)} / ${total}`}
        </span>
        <select value={cameraId} onChange={(e) => setCameraId(e.target.value)}>
          <option value="">{t('Recordings.AllCameras')}</option>
          {cameras.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
      </div>

      {items === null ? (
        <div className="spin">{t('Common.Loading')}</div>
      ) : items.length === 0 ? (
        <p className="muted">{t('Snapshots.Empty')}</p>
      ) : (
        <div className="gallery">
          {items.map((s) => (
            <button key={s.id} className="shot" onClick={() => setOpen(s)}>
              <img src={api.snapshotImageUrl(s.id, true)} alt="" loading="lazy" />
              <span className="cap">
                <span className="when">{new Date(s.takenAt).toLocaleString()}</span>
                <span className="who">{s.cameraName ?? '—'}</span>
              </span>
            </button>
          ))}
        </div>
      )}

      {pageCount > 1 && (
        <div className="pager">
          <button disabled={page === 0} onClick={() => void flip(page - 1)}>
            <Icon name="chevronLeft" size={14} />
          </button>
          <span className="muted">{page + 1} / {pageCount}</span>
          <button disabled={page + 1 >= pageCount} onClick={() => void flip(page + 1)}>
            <Icon name="chevronRight" size={14} />
          </button>
        </div>
      )}

      {open && (
        <div className="backdrop" onClick={() => setOpen(null)}>
          <div className="modal snapshot-modal" onClick={(e) => e.stopPropagation()}>
            <h2>{open.cameraName ?? '—'}</h2>
            <img src={api.snapshotImageUrl(open.id)} alt="" />
            <p className="muted" style={{ fontSize: 12 }}>
              {new Date(open.takenAt).toLocaleString()}
              {open.width > 0 && ` · ${open.width}×${open.height}`}
            </p>
            <div className="modal-actions">
              <button onClick={() => setOpen(null)}>{t('Common.Close')}</button>
              {can('Manage') && (
                <button className="danger" onClick={() => setDeleting(open)}>
                  {t('Cameras.Delete')}
                </button>
              )}
              <a className="button-link row" href={api.snapshotImageUrl(open.id, false, true)} download>
                <Icon name="download" /> {t('Snapshot.Download')}
              </a>
            </div>
          </div>
        </div>
      )}

      {deleting && (
        <ConfirmModal
          title={t('Snapshots.Delete')}
          message={t('Snapshots.DeleteConfirm')}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={remove}
          onCancel={() => setDeleting(null)}
        />
      )}
    </div>
  )
}
