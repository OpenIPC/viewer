import { useEffect, useState } from 'react'
import { api } from '../api'
import { useI18n } from '../i18n'
import { useAuth } from '../auth'
import { Icon } from './Icon'

// A still, taken on demand and shown full size with a download link.
//
// The image is fetched as a blob rather than pointed at with <img src>: the
// endpoint answers 502 when the camera doesn't respond, and an <img> would just
// show a broken icon with no way to say why. A blob URL also makes the download
// keep the name we choose instead of "snapshot".
export function SnapshotModal({ cameraId, cameraName, onClose }: {
  cameraId: string
  cameraName: string
  onClose: () => void
}) {
  const { t } = useI18n()
  const { can } = useAuth()
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)

  // Saving re-takes the shot rather than uploading the bytes on screen: the
  // browser holding a JPEG is not a reason to trust it with what lands in the
  // library, and the second capture is one HTTP call to a camera that has just
  // proved it answers.
  const save = async () => {
    setSaving(true)
    try {
      await api.saveSnapshot(cameraId)
      setSaved(t('Snapshot.Saved'))
    } catch {
      setSaved(t('Snapshot.SaveFailed'))
    } finally {
      setSaving(false)
    }
  }

  useEffect(() => {
    let revoked: string | null = null
    let cancelled = false
    void (async () => {
      try {
        const res = await fetch(api.snapshotUrl(cameraId), { credentials: 'same-origin' })
        if (!res.ok) throw new Error(String(res.status))
        const blob = await res.blob()
        if (cancelled) return
        revoked = URL.createObjectURL(blob)
        setUrl(revoked)
      } catch {
        if (!cancelled) setError(true)
      }
    })()
    return () => {
      cancelled = true
      if (revoked) URL.revokeObjectURL(revoked)
    }
  }, [cameraId])

  // Local time in the filename, since that's the clock the viewer thinks in.
  const stamp = new Date()
    .toLocaleString('sv-SE')
    .replace(/[: ]/g, '-')
  const fileName = `${cameraName.replace(/[^\w.-]+/g, '_')}-${stamp}.jpg`

  return (
    <div className="backdrop" onClick={onClose}>
      <div className="modal snapshot-modal" onClick={(e) => e.stopPropagation()}>
        <h2>{cameraName}</h2>
        {error ? (
          <p className="err">{t('Snapshot.Failed')}</p>
        ) : url ? (
          <img src={url} alt={cameraName} />
        ) : (
          <p className="muted">{t('Snapshot.Taking')}</p>
        )}
        {saved && <p className="muted">{saved}</p>}
        <div className="modal-actions">
          <button onClick={onClose}>{t('Common.Close')}</button>
          {url && can('ViewArchive') && (
            <button onClick={() => void save()} disabled={saving || saved !== null}>
              {t('Snapshot.Save')}
            </button>
          )}
          {url && (
            <a className="button-link row" href={url} download={fileName}>
              <Icon name="download" /> {t('Snapshot.Download')}
            </a>
          )}
        </div>
      </div>
    </div>
  )
}
