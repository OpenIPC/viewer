import { useEffect, useState } from 'react'
import { api, type CalendarPointDto, type CameraDto, type RecordingDto } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'
import { ConfirmModal } from '../components/Modals'
import { ArchiveCalendar, dayRange, monthRange } from '../components/ArchiveCalendar'

// The recorded archive: what the desktop head wrote, listed newest-first and
// played in place. Playback is a plain <video> against a ranged endpoint, so
// seeking is the browser's own — no player to maintain.
export function Recordings() {
  const { t } = useI18n()
  const { can } = useAuth()
  const [items, setItems] = useState<RecordingDto[] | null>(null)
  const [cameras, setCameras] = useState<CameraDto[]>([])
  const [cameraId, setCameraId] = useState('')
  const [month, setMonth] = useState(() => new Date())
  const [day, setDay] = useState<string | null>(null)
  const [points, setPoints] = useState<CalendarPointDto[]>([])
  const [playing, setPlaying] = useState<RecordingDto | null>(null)
  const [deleting, setDeleting] = useState<RecordingDto | null>(null)

  // The list shows the selected day (or everything when none is picked); the
  // calendar always shows the whole displayed month, so the dots stay put while
  // you click around inside it.
  const load = async () => {
    const range = day ? dayRange(day) : {}
    const [list, dots, cams] = await Promise.all([
      api.recordings({ cameraId: cameraId || undefined, ...range }),
      api.recordingCalendar({ cameraId: cameraId || undefined, ...monthRange(month) }),
      cameras.length ? Promise.resolve(cameras) : api.cameras(),
    ])
    setItems(list)
    setPoints(dots)
    setCameras(cams)
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameraId, day, month])

  if (items === null) return <div className="spin">{t('Common.Loading')}</div>

  return (
    <div className="wrap" style={{ maxWidth: 1000 }}>
      <div className="toolbar">
        <h1 style={{ margin: 0, flex: 1 }}>{t('Recordings.Title')}</h1>
        <select value={cameraId} onChange={(e) => setCameraId(e.target.value)}>
          <option value="">{t('Recordings.AllCameras')}</option>
          {cameras.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
      </div>

      <ArchiveCalendar
        month={month}
        points={points}
        selected={day}
        onPickMonth={(next) => {
          setMonth(next)
          setDay(null)   // a day from the old month would filter to nothing
        }}
        onPickDay={setDay}
      />

      {playing && (
        <div className="player">
          <video src={api.recordingStreamUrl(playing.id)} controls autoPlay />
          <div className="row" style={{ justifyContent: 'space-between', marginTop: 8 }}>
            <span className="muted">
              {playing.cameraName ?? '—'} · {formatStart(playing.startedAt)}
            </span>
            <span className="row">
              <a className="button-link row" href={api.recordingStreamUrl(playing.id, true)}>
                <Icon name="download" /> {t('Recordings.Download')}
              </a>
              <button onClick={() => setPlaying(null)}>{t('Common.Close')}</button>
            </span>
          </div>
        </div>
      )}

      {items.length === 0 ? (
        <p className="muted">{day ? t('Archive.EmptyDay') : t('Recordings.Empty')}</p>
      ) : (
        <table className="list">
          <thead>
            <tr>
              <th>{t('Recordings.Th.Camera')}</th>
              <th>{t('Recordings.Th.Started')}</th>
              <th>{t('Recordings.Th.Duration')}</th>
              <th>{t('Recordings.Th.Size')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((r) => (
              <tr key={r.id}>
                <td>
                  {r.cameraName ?? '—'}
                  {r.hasMotion && <span className="badge" style={{ marginLeft: 8 }}>{t('Recordings.Motion')}</span>}
                  {!r.playable && (
                    <span className="badge" style={{ marginLeft: 8 }} title={t('Recordings.NotPlayableHint')}>
                      {r.codec ?? 'codec'}
                    </span>
                  )}
                </td>
                <td className="mono">{formatStart(r.startedAt)}</td>
                <td>{formatDuration(r.durationSeconds)}</td>
                <td className="muted">{formatSize(r.sizeBytes)}</td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  {r.playable ? (
                    <button className="row" onClick={() => setPlaying(r)}>
                      <Icon name="play" size={13} /> {t('Recordings.Play')}
                    </button>
                  ) : (
                    <a className="muted row" href={api.recordingStreamUrl(r.id, true)}>
                      <Icon name="download" size={13} /> {t('Recordings.Download')}
                    </a>
                  )}{' '}
                  {can('Manage') && (
                    <button className="danger" onClick={() => setDeleting(r)}>
                      {t('Cameras.Delete')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {deleting && (
        <ConfirmModal
          title={t('Recordings.DeleteConfirm')}
          message={`${deleting.cameraName ?? ''} ${formatStart(deleting.startedAt)}`}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={async () => {
            const target = deleting
            setDeleting(null)
            if (playing?.id === target.id) setPlaying(null)
            await api.deleteRecording(target.id)
            await load()
          }}
          onCancel={() => setDeleting(null)}
        />
      )}
    </div>
  )
}

function formatStart(iso: string): string {
  return new Date(iso).toLocaleString()
}

// Null duration = the recording never got an end time (the app died mid-write).
function formatDuration(seconds: number | null): string {
  if (seconds === null) return '—'
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  const pad = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`
}

function formatSize(bytes: number): string {
  if (bytes <= 0) return '—'
  const mb = bytes / (1024 * 1024)
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb.toFixed(1)} MB`
}
