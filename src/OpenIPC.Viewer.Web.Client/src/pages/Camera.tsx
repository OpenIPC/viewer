import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, type CameraDto, type GroupDto } from '../api'
import { useI18n } from '../i18n'
import { useAuth } from '../auth'
import { LiveTile } from '../components/LiveTile'
import { CameraEditor } from '../components/CameraEditor'
import { PtzPad } from '../components/PtzPad'
import { ConfirmModal } from '../components/Modals'

// Dedicated single-camera view: one large live tile plus the camera's details
// and actions. This is the natural shape on a phone (you watch one camera at a
// time), and it replaces the old /grid?only=<id> shortcut.
export function Camera() {
  const { id = '' } = useParams()
  const { t } = useI18n()
  const { can } = useAuth()
  const navigate = useNavigate()
  const [camera, setCamera] = useState<CameraDto | null | undefined>(undefined)
  const [groups, setGroups] = useState<GroupDto[]>([])
  const [editing, setEditing] = useState(false)
  const [deleting, setDeleting] = useState(false)

  const load = async () => {
    const [cams, grps] = await Promise.all([api.cameras(), api.groups()])
    setCamera(cams.find((c) => c.id === id) ?? null)
    setGroups(grps)
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  const onDelete = async () => {
    setDeleting(false)
    await api.deleteCamera(id)
    navigate('/cameras', { replace: true })
  }

  if (camera === undefined) return <div className="spin">{t('Common.Loading')}</div>
  if (camera === null)
    return (
      <div className="wrap">
        <p className="muted">{t('Grid.Empty')}</p>
        <Link to="/cameras">{t('Live.Back')}</Link>
      </div>
    )

  return (
    <div className="wrap" style={{ maxWidth: 1000 }}>
      <div className="toolbar">
        <Link to="/cameras" className="muted">
          {t('Live.Back')}
        </Link>
        <h1 style={{ margin: 0, flex: 1 }}>{camera.name}</h1>
        {can('Manage') && (
          <>
            <button onClick={() => setEditing(true)}>{t('Cameras.Edit')}</button>
            <button className="danger" onClick={() => setDeleting(true)}>
              {t('Cameras.Delete')}
            </button>
          </>
        )}
      </div>

      <div className="videos n1">
        <LiveTile key={camera.id} camera={camera} />
      </div>

      {camera.ptzReady && can('Ptz') && (
        <>
          <h2 style={{ fontSize: 16, fontWeight: 600, marginTop: 24 }}>{t('Ptz.Title')}</h2>
          <PtzPad cameraId={camera.id} />
        </>
      )}

      <h2 style={{ fontSize: 16, fontWeight: 600, marginTop: 24 }}>{t('Camera.Details')}</h2>
      <table className="details">
        <tbody>
          <Row label={t('Cameras.Th.Host')} value={<span className="mono">{camera.host}</span>} />
          <Row label={t('Cameras.Th.Group')} value={camera.groupName ?? '—'} />
          <Row label={t('Editor.Quality')} value={camera.streamQuality} />
          <Row label={t('Editor.RtspMain')} value={<span className="mono break">{camera.rtspMain}</span>} />
          {camera.rtspSub && (
            <Row label={t('Editor.RtspSub')} value={<span className="mono break">{camera.rtspSub}</span>} />
          )}
          <Row label={t('Cameras.Th.Grid')} value={camera.includedInGrid ? t('Common.Yes') : t('Common.No')} />
          <Row label={t('Camera.Credentials')} value={camera.hasCredentials ? t('Common.Yes') : t('Common.No')} />
          <Row
            label={t('Camera.Capabilities')}
            value={
              <span className="row" style={{ flexWrap: 'wrap', gap: 6 }}>
                {capabilityBadges(camera).length === 0 ? (
                  <span className="muted">—</span>
                ) : (
                  capabilityBadges(camera).map((c) => (
                    <span key={c} className="badge">
                      {c}
                    </span>
                  ))
                )}
              </span>
            }
          />
          {camera.chipModel && <Row label={t('Camera.Chip')} value={camera.chipModel} />}
          {camera.firmwareVersion && <Row label={t('Camera.Firmware')} value={camera.firmwareVersion} />}
        </tbody>
      </table>

      {editing && (
        <CameraEditor
          camera={camera}
          groups={groups}
          onClose={() => setEditing(false)}
          onSaved={() => {
            setEditing(false)
            void load()
          }}
        />
      )}
      {deleting && (
        <ConfirmModal
          title={t('Cameras.DeleteConfirm')}
          message={camera.name}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={onDelete}
          onCancel={() => setDeleting(false)}
        />
      )}
    </div>
  )
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <tr>
      <td className="muted" style={{ width: 180 }}>
        {label}
      </td>
      <td>{value}</td>
    </tr>
  )
}

// Capability chips straight off the camera record. PTZ additionally needs a
// probed media profile before the controls appear — see camera.ptzReady above.
function capabilityBadges(c: CameraDto): string[] {
  const out: string[] = []
  if (c.hasPtz) out.push('PTZ')
  if (c.isMajestic) out.push('Majestic')
  if (c.onvifEnabled) out.push('ONVIF')
  if (c.hasAudioIn) out.push('Audio in')
  if (c.hasAudioOut) out.push('Audio out')
  return out
}
