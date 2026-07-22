import { useEffect, useRef, useState } from 'react'
import { api, type CalendarPointDto, type CameraDto, type RecordingDto } from '../api'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'
import { ConfirmModal } from '../components/Modals'
import { ArchiveCalendar, dayRange, monthRange } from '../components/ArchiveCalendar'
import { ArchiveTabs } from '../components/ArchiveTabs'

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
  const [page, setPage] = useState(0)
  const [total, setTotal] = useState(0)
  const [playing, setPlaying] = useState<RecordingDto | null>(null)
  const [playhead, setPlayhead] = useState(0)
  const [clipStart, setClipStart] = useState(0)
  const [clipEnd, setClipEnd] = useState<number | null>(null)
  const [precise, setPrecise] = useState(false)
  const videoRef = useRef<HTMLVideoElement>(null)
  const [deleting, setDeleting] = useState<RecordingDto | null>(null)

  // The list shows the selected day (or everything when none is picked); the
  // calendar always shows the whole displayed month, so the dots stay put while
  // you click around inside it.
  const load = async (pageIndex = page) => {
    const range = day ? dayRange(day) : {}
    const [result, dots, cams] = await Promise.all([
      api.recordings({
        cameraId: cameraId || undefined,
        ...range,
        offset: pageIndex * PAGE_SIZE,
        limit: PAGE_SIZE,
      }),
      api.recordingCalendar({ cameraId: cameraId || undefined, ...monthRange(month) }),
      cameras.length ? Promise.resolve(cameras) : api.cameras(),
    ])
    setItems(result.items)
    setTotal(result.total)
    setPoints(dots)
    setCameras(cams)
  }

  // Changing WHAT is being looked at starts over at the first page; paging
  // through the same selection does not.
  useEffect(() => {
    setPage(0)
    void load(0)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameraId, day, month])

  useEffect(() => {
    void load(page)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page])

  if (items === null) return <div className="spin">{t('Common.Loading')}</div>

  return (
    <div className="wrap" style={{ maxWidth: 1000 }}>
      <h1>{t('Nav.Recordings')}</h1>
      <ArchiveTabs />
      <div className="toolbar">
        <span style={{ flex: 1 }} />
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
          <video
            ref={videoRef}
            src={api.recordingStreamUrl(playing.id)}
            controls
            autoPlay
            onTimeUpdate={(e) => setPlayhead(e.currentTarget.currentTime)}
          />
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

          {can('Export') && (
            <div className="export row">
              {/* Marks come from the playhead: scrub to the moment and press the
                  button. Typing timecodes would be worse everywhere, phones most. */}
              <button onClick={() => setClipStart(playhead)}>{t('Export.MarkIn')}</button>
              <button onClick={() => setClipEnd(playhead)}>{t('Export.MarkOut')}</button>
              <span className="muted mono">
                {formatClock(clipStart)} &ndash; {clipEnd === null ? '--:--' : formatClock(clipEnd)}
              </span>
              <label className="row" style={{ flexDirection: 'row', gap: 6 }}>
                <input type="checkbox" checked={precise} onChange={(e) => setPrecise(e.target.checked)} />
                {t('Export.Precise')}
              </label>
              <a
                className={'button-link row' + (clipEnd !== null && clipEnd > clipStart ? '' : ' disabled')}
                href={
                  clipEnd !== null && clipEnd > clipStart
                    ? api.recordingExportUrl(playing.id, clipStart, clipEnd, precise)
                    : undefined
                }
              >
                <Icon name="download" /> {t('Export.Save')}
              </a>
            </div>
          )}
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
                    <button
                      className="row"
                      onClick={() => {
                        setPlaying(r)
                        setPlayhead(0)
                        setClipStart(0)
                        setClipEnd(null)
                      }}
                    >
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

      {total > PAGE_SIZE && (
        <div className="pager">
          <button disabled={page === 0} title={t('Grid.PrevPage')} onClick={() => setPage(page - 1)}>
            <Icon name="chevronLeft" size={14} />
          </button>
          <span className="muted">
            {page * PAGE_SIZE + 1}&ndash;{Math.min((page + 1) * PAGE_SIZE, total)} / {total}
          </span>
          <button
            disabled={(page + 1) * PAGE_SIZE >= total}
            title={t('Grid.NextPage')}
            onClick={() => setPage(page + 1)}
          >
            <Icon name="chevronRight" size={14} />
          </button>
        </div>
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

const PAGE_SIZE = 50

// mm:ss for the in/out marks: a clip offset is seconds into one file, not a date.
function formatClock(seconds: number): string {
  const m = Math.floor(seconds / 60)
  const s = Math.floor(seconds % 60)
  return `${m}:${String(s).padStart(2, '0')}`
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
