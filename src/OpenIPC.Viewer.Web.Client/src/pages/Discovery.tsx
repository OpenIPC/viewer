import { useEffect, useRef, useState } from 'react'
import { api, ApiError, type CameraDraftDto, type DiscoveredDeviceDto, type ScanDto } from '../api'
import { Link } from 'react-router-dom'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'

// Find cameras on the LAN and add them without leaving the browser.
//
// The scan runs server-side as a job; this page polls it while it's running, so
// navigating away doesn't kill it and coming back re-attaches to the same run.
// Adding is deliberately two steps — probe, review, add — because ONVIF is the
// only source of the real RTSP URI and it can be wrong behind NAT.
const POLL_MS = 1000

export function Discovery() {
  const { t } = useI18n()
  const [scan, setScan] = useState<ScanDto | null>(null)
  const [deepScan, setDeepScan] = useState(false)
  const [knownHosts, setKnownHosts] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<DiscoveredDeviceDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Credentials are reused across candidates within the page — a site's cameras
  // almost always share one login, and retyping it per device is a chore.
  const [creds, setCreds] = useState({ username: '', password: '' })
  const timer = useRef<number | null>(null)

  const loadKnown = async () => {
    const cams = await api.cameras()
    setKnownHosts(new Set(cams.map((c) => c.host.toLowerCase())))
  }

  useEffect(() => {
    void loadKnown()
    return () => {
      if (timer.current !== null) window.clearInterval(timer.current)
    }
  }, [])

  const poll = (id: string) => {
    if (timer.current !== null) window.clearInterval(timer.current)
    timer.current = window.setInterval(async () => {
      try {
        const s = await api.getScan(id)
        setScan(s)
        if (s.status !== 'running' && timer.current !== null) {
          window.clearInterval(timer.current)
          timer.current = null
        }
      } catch {
        if (timer.current !== null) window.clearInterval(timer.current)
        timer.current = null
      }
    }, POLL_MS)
  }

  const start = async () => {
    setError(null)
    try {
      const s = await api.startScan(deepScan)
      setScan(s)
      poll(s.id)
    } catch (e) {
      setError(e instanceof ApiError && e.code === 'scan_in_progress'
        ? t('Discovery.Busy')
        : t('Discovery.Failed'))
    }
  }

  const stop = async () => {
    if (!scan) return
    await api.cancelScan(scan.id).catch(() => undefined)
  }

  const running = scan?.status === 'running'

  return (
    <div className="wrap" style={{ maxWidth: 900 }}>
      <div className="toolbar" style={{ flexWrap: 'wrap' }}>
        <Link to="/cameras" className="muted row">
          <Icon name="back" /> {t('Nav.Cameras')}
        </Link>
        <h1 style={{ margin: 0, flex: 1 }}>{t('Discovery.Title')}</h1>
        <label className="row" style={{ flexDirection: 'row', gap: 6 }}>
          <input type="checkbox" checked={deepScan} disabled={running}
                 onChange={(e) => setDeepScan(e.target.checked)} />
          {t('Discovery.DeepScan')}
        </label>
        {running ? (
          <button onClick={() => void stop()}>{t('Discovery.Stop')}</button>
        ) : (
          <button className="primary" onClick={() => void start()}>{t('Discovery.Scan')}</button>
        )}
      </div>

      <p className="muted" style={{ fontSize: 12, marginTop: -8 }}>{t('Discovery.DeepScanNote')}</p>
      {error && <p className="err">{error}</p>}

      {running && (
        <div className="progress" title={`${Math.round((scan?.progress ?? 0) * 100)}%`}>
          <span style={{ width: `${Math.round((scan?.progress ?? 0) * 100)}%` }} />
        </div>
      )}

      {scan && (
        <p className="muted">
          {scan.status === 'running' && t('Discovery.Scanning')}
          {scan.status === 'done' && `${t('Discovery.Found')} ${scan.devices.length}`}
          {scan.status === 'cancelled' && t('Discovery.Cancelled')}
          {scan.status === 'failed' && `${t('Discovery.Failed')} ${scan.error ?? ''}`}
        </p>
      )}

      {scan && scan.devices.length > 0 && (
        <table className="list">
          <thead>
            <tr>
              <th>{t('Discovery.Th.Device')}</th>
              <th>{t('Discovery.Th.Host')}</th>
              <th>{t('Discovery.Th.Signals')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {scan.devices.map((d) => {
              const known = knownHosts.has(d.host.toLowerCase())
              return (
                <tr key={d.host}>
                  <td>
                    {d.name ?? d.model ?? '—'}
                    <span className={'badge ' + confidenceClass(d.confidence)} style={{ marginLeft: 8 }}>
                      {t('Discovery.Confidence.' + d.confidence)}
                    </span>
                  </td>
                  <td className="mono">
                    {d.host}
                    {d.ports.length > 0 && <span className="muted"> :{d.ports.join(', ')}</span>}
                  </td>
                  <td className="muted">{d.protocols.join(' · ') || '—'}</td>
                  <td style={{ textAlign: 'right' }}>
                    {known ? (
                      <span className="muted">{t('Discovery.AlreadyAdded')}</span>
                    ) : (
                      <button onClick={() => setSelected(d)}>{t('Discovery.Add')}</button>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}

      {scan && scan.status !== 'running' && scan.devices.length === 0 && (
        <p className="muted">{t('Discovery.Nothing')}</p>
      )}

      {selected && (
        <AddDeviceModal
          device={selected}
          creds={creds}
          onCreds={setCreds}
          onClose={() => setSelected(null)}
          onAdded={async () => {
            setSelected(null)
            await loadKnown()
          }}
        />
      )}
    </div>
  )
}

// Probe → review → add. The probe is optional: a device that doesn't speak
// ONVIF can still be added with the guessed RTSP URI the server returns.
function AddDeviceModal({
  device, creds, onCreds, onClose, onAdded,
}: {
  device: DiscoveredDeviceDto
  creds: { username: string; password: string }
  onCreds: (c: { username: string; password: string }) => void
  onClose: () => void
  onAdded: () => void
}) {
  const { t } = useI18n()
  const [draft, setDraft] = useState<CameraDraftDto | null>(null)
  const [name, setName] = useState(device.name ?? device.model ?? device.host)
  const [rtsp, setRtsp] = useState('')
  const [onvifPort, setOnvifPort] = useState(String(device.onvifPort ?? 80))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const probe = async () => {
    setBusy(true)
    setError(null)
    try {
      const d = await api.probeDevice({
        host: device.host,
        onvifPort: Number(onvifPort) || 80,
        username: creds.username,
        password: creds.password,
      })
      setDraft(d)
      setRtsp(d.rtspMain)
      if (d.onvifOk) setName(d.suggestedName)
    } catch {
      setError(t('Discovery.ProbeFailed'))
    } finally {
      setBusy(false)
    }
  }

  const add = async () => {
    setBusy(true)
    setError(null)
    try {
      await api.addDiscovered({
        host: device.host,
        name: name.trim(),
        onvifPort: Number(onvifPort) || null,
        username: creds.username || undefined,
        password: creds.password || undefined,
        rtspMain: rtsp || undefined,
      })
      onAdded()
    } catch (e) {
      setError(e instanceof ApiError && e.details?.length ? e.details.join(', ') : t('Discovery.AddFailed'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h2>{t('Discovery.AddTitle')} — <span className="mono">{device.host}</span></h2>
        <div className="form-grid">
          <div className="two">
            <label>
              {t('Editor.Username')}
              <input value={creds.username} onChange={(e) => onCreds({ ...creds, username: e.target.value })} />
            </label>
            <label>
              {t('Editor.Password')}
              <input type="password" value={creds.password}
                     onChange={(e) => onCreds({ ...creds, password: e.target.value })} />
            </label>
          </div>
          <div className="two">
            <label>
              {t('Editor.OnvifPort')}
              <input value={onvifPort} onChange={(e) => setOnvifPort(e.target.value)} />
            </label>
            <label>
              {t('Editor.Name')}
              <input value={name} onChange={(e) => setName(e.target.value)} />
            </label>
          </div>
          <label>
            {t('Editor.RtspMain')}
            <input value={rtsp} placeholder={t('Discovery.ProbeFirst')}
                   onChange={(e) => setRtsp(e.target.value)} />
          </label>

          {draft && (
            <p className={draft.onvifOk ? 'muted' : 'err'} style={{ fontSize: 12, margin: 0 }}>
              {draft.onvifOk ? (
                <>
                  {[draft.manufacturer, draft.model, draft.firmwareVersion].filter(Boolean).join(' · ')}
                  {draft.hasPtz && <span className="badge" style={{ marginLeft: 8 }}>PTZ</span>}
                  {draft.hasAudioIn && <span className="badge" style={{ marginLeft: 6 }}>Audio in</span>}
                  {draft.hasAudioOut && <span className="badge" style={{ marginLeft: 6 }}>Audio out</span>}
                </>
              ) : (
                t('Discovery.NoOnvif')
              )}
            </p>
          )}
          {error && <p className="err" style={{ margin: 0 }}>{error}</p>}
        </div>

        <div className="modal-actions">
          <button onClick={onClose}>{t('Common.Cancel')}</button>
          <button onClick={() => void probe()} disabled={busy}>{t('Discovery.Probe')}</button>
          <button className="primary" onClick={() => void add()} disabled={busy || !name.trim()}>
            {t('Discovery.Add')}
          </button>
        </div>
      </div>
    </div>
  )
}

function confidenceClass(c: string): string {
  return c === 'High' ? 'on' : ''
}
