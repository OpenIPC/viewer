import { useEffect, useMemo, useState } from 'react'
import { api, ApiError, type MajesticDto, type MajesticFieldDto } from '../api'
import { useI18n } from '../i18n'
import { Icon } from './Icon'

// The camera's own settings, on the camera page.
//
// Schema-driven like the desktop editor: the server flattens the live
// config.json and we render a control per scalar knob, because the Majestic
// schema drifts between firmware builds and a hardcoded form would show fields
// that aren't there and hide the ones that are.
//
// The ISP section gets its own panel above the rest. Not cosmetic: Majestic
// re-reads those live, so changing exposure or flip is the one edit that does
// NOT restart the encoder — and while you're tuning a picture, restarting the
// stream on every nudge makes the job impossible.
export function MajesticPanel({ cameraId }: { cameraId: string }) {
  const { t } = useI18n()
  const [data, setData] = useState<MajesticDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  // A camera that doesn't run Majestic isn't an error to report — there is
  // simply no panel for it, so the section disappears entirely.
  const [absent, setAbsent] = useState(false)
  const [values, setValues] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [showMetrics, setShowMetrics] = useState(false)
  const [showRaw, setShowRaw] = useState(false)

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      const d = await api.majestic(cameraId)
      setData(d)
      setValues(Object.fromEntries(d.sections.flatMap((s) => s.fields).map((f) => [f.path, f.value])))
    } catch (e) {
      if (e instanceof ApiError && e.code === 'majestic_unavailable') setAbsent(true)
      else setError(t('Majestic.Unreachable'))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
    // Reloading when the camera changes is the whole dependency; `load` is
    // recreated every render by design.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameraId])

  const allFields = useMemo(() => data?.sections.flatMap((s) => s.fields) ?? [], [data])
  const isp = useMemo(() => allFields.filter((f) => f.section === 'isp'), [allFields])
  const rest = useMemo(() => data?.sections.filter((s) => s.name !== 'isp') ?? [], [data])

  const changed = (f: MajesticFieldDto) => !sameValue(f, values[f.path] ?? f.value)
  const changedIn = (fields: MajesticFieldDto[]) => fields.filter(changed)

  const apply = async (fields: MajesticFieldDto[]) => {
    const edits = changedIn(fields).map((f) => ({ path: f.path, value: values[f.path] }))
    if (edits.length === 0) {
      setStatus(t('Majestic.NoChanges'))
      return
    }
    setBusy(true)
    setStatus(null)
    try {
      const res = await api.majesticApply(cameraId, edits)
      setStatus(res.restart ? t('Majestic.AppliedRestart') : t('Majestic.Applied'))
      // Re-read rather than trust the local copy: the camera normalises values
      // (and may refuse one), so the panel should show what it actually holds.
      await load()
    } catch {
      setStatus(t('Majestic.ApplyFailed'))
    } finally {
      setBusy(false)
    }
  }

  const revert = () => {
    if (!data) return
    setValues(Object.fromEntries(allFields.map((f) => [f.path, f.value])))
    setStatus(null)
  }

  const setNight = async (mode: 'day' | 'night' | 'auto') => {
    setBusy(true)
    try {
      await api.majesticNight(cameraId, mode)
      await load()
    } catch {
      setStatus(t('Majestic.ApplyFailed'))
    } finally {
      setBusy(false)
    }
  }

  if (absent) return null
  if (loading) return <div className="spin">{t('Common.Loading')}</div>
  if (error || !data) return <p className="muted">{error}</p>

  const needle = filter.trim().toLowerCase()
  const visibleSections = rest
    .map((s) => ({
      name: s.name,
      fields: needle ? s.fields.filter((f) => f.path.toLowerCase().includes(needle)) : s.fields,
    }))
    .filter((s) => s.fields.length > 0)
  const dirty = changedIn(allFields).length

  return (
    <section className="majestic">
      <div className="toolbar">
        <h2 style={{ margin: 0, fontSize: 15 }}>{t('Majestic.Title')}</h2>
        <span className="muted" style={{ flex: 1, fontSize: 12 }}>
          {[data.info?.model, data.info?.firmware, data.info?.chip, data.info?.uptime]
            .filter(Boolean)
            .join(' · ')}
        </span>
        <button className="row" onClick={() => void load()} disabled={busy}>
          <Icon name="refresh" size={13} /> {t('Majestic.Reload')}
        </button>
      </div>

      <div className="row" style={{ marginBottom: 12, flexWrap: 'wrap' }}>
        <span className="muted" style={{ fontSize: 12 }}>{t('Majestic.NightMode')}:</span>
        {(['day', 'night', 'auto'] as const).map((m) => (
          <button
            key={m}
            className={data.nightMode === m ? 'active' : ''}
            disabled={busy}
            onClick={() => void setNight(m)}
          >
            {t('Majestic.Night.' + m)}
          </button>
        ))}
      </div>

      {isp.length > 0 && (
        <div className="majestic-block">
          <div className="toolbar">
            <h3>{t('Majestic.Isp')}</h3>
            <span className="muted" style={{ flex: 1, fontSize: 12 }}>{t('Majestic.IspHint')}</span>
            <button className="primary" disabled={busy} onClick={() => void apply(isp)}>
              {t('Majestic.ApplyIsp')}
            </button>
          </div>
          <div className="fields">
            {isp.map((f) => (
              <FieldRow
                key={f.path}
                field={f}
                value={values[f.path] ?? f.value}
                modified={changed(f)}
                onChange={(v) => setValues((p) => ({ ...p, [f.path]: v }))}
              />
            ))}
          </div>
        </div>
      )}

      <div className="majestic-block">
        <div className="toolbar">
          <h3>{t('Majestic.Config')}</h3>
          <input
            className="filter"
            placeholder={t('Majestic.Filter')}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
          <span style={{ flex: 1 }} />
          {dirty > 0 && <span className="muted" style={{ fontSize: 12 }}>{t('Majestic.Changed')}: {dirty}</span>}
          <button disabled={busy || dirty === 0} onClick={revert}>{t('Majestic.Revert')}</button>
          <button className="primary" disabled={busy} onClick={() => void apply(allFields)}>
            {t('Common.Save')}
          </button>
        </div>
        {visibleSections.map((s) => (
          <div key={s.name} className="majestic-section">
            <h4>{s.name}</h4>
            <div className="fields">
              {s.fields.map((f) => (
                <FieldRow
                  key={f.path}
                  field={f}
                  value={values[f.path] ?? f.value}
                  modified={changed(f)}
                  onChange={(v) => setValues((p) => ({ ...p, [f.path]: v }))}
                />
              ))}
            </div>
          </div>
        ))}
        {visibleSections.length === 0 && <p className="muted">{t('Majestic.NoMatches')}</p>}
      </div>

      {status && <p className="muted">{status}</p>}

      <div className="majestic-block">
        <button className="row" onClick={() => setShowMetrics(!showMetrics)}>
          <Icon name={showMetrics ? 'chevronDown' : 'chevronRight'} size={13} /> {t('Majestic.Metrics')}
        </button>
        {showMetrics && <Metrics cameraId={cameraId} />}
      </div>

      <div className="majestic-block">
        <button className="row" onClick={() => setShowRaw(!showRaw)}>
          <Icon name={showRaw ? 'chevronDown' : 'chevronRight'} size={13} /> {t('Majestic.Raw')}
        </button>
        {showRaw && <RawEditor cameraId={cameraId} initial={data.rawJson} onSaved={load} />}
      </div>
    </section>
  )
}

// One knob. The control follows the field's kind, and the value stays a string
// end-to-end (the server coerces it back to the right JSON type on write-back).
function FieldRow({ field, value, modified, onChange }: {
  field: MajesticFieldDto
  value: string
  modified: boolean
  onChange: (v: string) => void
}) {
  const { t } = useI18n()
  return (
    <label className={'field' + (modified ? ' modified' : '')}>
      <span className="k">
        {field.key}
        {field.restart && <span className="restart" title={t('Majestic.RestartHint')}>↻</span>}
      </span>
      {field.kind === 'bool' ? (
        <input
          type="checkbox"
          checked={value === 'true'}
          onChange={(e) => onChange(e.target.checked ? 'true' : 'false')}
        />
      ) : field.kind === 'enum' && field.options ? (
        <select value={value} onChange={(e) => onChange(e.target.value)}>
          {/* A live value outside the known set must not be silently rewritten. */}
          {!field.options.includes(value) && <option value={value}>{value}</option>}
          {field.options.map((o) => (
            <option key={o} value={o}>{o}</option>
          ))}
        </select>
      ) : (
        <input
          type={field.kind === 'int' || field.kind === 'number' ? 'number' : 'text'}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      )}
    </label>
  )
}

// Read-only telemetry from the camera's /metrics. Loaded on expand, never
// polled: this is a "why is it hot" glance, not a dashboard.
function Metrics({ cameraId }: { cameraId: string }) {
  const { t } = useI18n()
  const [rows, setRows] = useState<{ name: string; value: number }[] | null>(null)
  const [failed, setFailed] = useState(false)

  const load = async () => {
    setFailed(false)
    try {
      setRows(await api.majesticMetrics(cameraId))
    } catch {
      setFailed(true)
    }
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameraId])

  if (failed) return <p className="muted">{t('Majestic.NoMetrics')}</p>
  if (!rows) return <div className="spin">{t('Common.Loading')}</div>
  return (
    <>
      <button className="row" onClick={() => void load()}>
        <Icon name="refresh" size={13} /> {t('Majestic.Reload')}
      </button>
      <table className="list metrics">
        <tbody>
          {rows.map((r) => (
            <tr key={r.name}>
              <td>{r.name}</td>
              <td style={{ textAlign: 'right' }}>{r.value}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}

// The escape hatch: the whole config.json as text. Parsed locally before it can
// be sent, so a typo is caught here rather than by a camera that then reboots
// into a broken config.
function RawEditor({ cameraId, initial, onSaved }: {
  cameraId: string
  initial: string
  onSaved: () => Promise<void>
}) {
  const { t } = useI18n()
  const [text, setText] = useState(() => pretty(initial))
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const valid = useMemo(() => {
    try {
      JSON.parse(text)
      return true
    } catch {
      return false
    }
  }, [text])

  const save = async () => {
    setBusy(true)
    setStatus(null)
    try {
      await api.majesticRaw(cameraId, text)
      setStatus(t('Majestic.Applied'))
      await onSaved()
    } catch {
      setStatus(t('Majestic.ApplyFailed'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <p className="muted" style={{ fontSize: 12 }}>{t('Majestic.RawHint')}</p>
      <textarea className="raw-json" value={text} onChange={(e) => setText(e.target.value)} rows={18} spellCheck={false} />
      <div className="row">
        <button className="primary" disabled={!valid || busy} onClick={() => void save()}>
          {t('Common.Save')}
        </button>
        {!valid && <span className="err">{t('Majestic.BadJson')}</span>}
        {status && <span className="muted">{status}</span>}
      </div>
    </>
  )
}

function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}

// Mirrors the server's kind-aware comparison, so "30" vs "30.0" doesn't light up
// as an edit and then get dropped as a no-op on save.
function sameValue(field: MajesticFieldDto, value: string): boolean {
  const a = (field.value ?? '').trim()
  const b = (value ?? '').trim()
  if (field.kind === 'bool') return a.toLowerCase() === b.toLowerCase()
  if (field.kind === 'int' || field.kind === 'number') {
    const na = Number(a)
    const nb = Number(b)
    if (!Number.isNaN(na) && !Number.isNaN(nb)) return na === nb
  }
  return a === b
}
