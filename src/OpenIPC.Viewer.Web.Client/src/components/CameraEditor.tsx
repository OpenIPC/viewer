import { useState, type FormEvent } from 'react'
import { api, ApiError, type CameraDto, type CameraWrite, type GroupDto } from '../api'
import { useI18n } from '../i18n'

const QUALITIES = ['Auto', 'AlwaysHd', 'AlwaysSd']

// Add/edit modal against the JSON camera API. On edit the password field is left
// blank and only sent when the user types one (matches the Phase 20 "leave blank
// to keep" semantics). Server 400 validation details are surfaced inline.
export function CameraEditor({
  camera,
  groups,
  onClose,
  onSaved,
}: {
  camera: CameraDto | null
  groups: GroupDto[]
  onClose: () => void
  onSaved: () => void
}) {
  const { t } = useI18n()
  const editing = camera !== null
  const [name, setName] = useState(camera?.name ?? '')
  const [host, setHost] = useState(camera?.host ?? '')
  const [rtspMain, setRtspMain] = useState(camera?.rtspMain ?? '')
  const [rtspSub, setRtspSub] = useState(camera?.rtspSub ?? '')
  const [httpPort, setHttpPort] = useState(String(camera?.httpPort ?? 80))
  const [onvifPort, setOnvifPort] = useState(camera?.onvifPort != null ? String(camera.onvifPort) : '')
  const [groupId, setGroupId] = useState(camera?.groupId != null ? String(camera.groupId) : '')
  const [quality, setQuality] = useState(camera?.streamQuality ?? 'Auto')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    setBusy(true)
    const body: CameraWrite = {
      name,
      host,
      rtspMain,
      rtspSub: rtspSub.trim() || null,
      httpPort: Number(httpPort) || 80,
      onvifPort: onvifPort.trim() ? Number(onvifPort) : null,
      groupId: groupId ? Number(groupId) : null,
      streamQuality: quality,
      // Only send credentials when both are typed. On edit, blank = keep existing.
      username: username.trim() || undefined,
      password: password ? password : undefined,
    }
    try {
      if (editing) await api.updateCamera(camera!.id, body)
      else await api.createCamera(body)
      onSaved()
    } catch (err) {
      if (err instanceof ApiError && err.details?.length) setError(err.details.join('; '))
      else setError(t('Editor.Error'))
      setBusy(false)
    }
  }

  return (
    <div className="backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h2>{editing ? t('Editor.EditTitle') : t('Editor.AddTitle')}</h2>
        <form onSubmit={onSubmit} className="form-grid">
          <label>
            {t('Editor.Name')}
            <input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </label>
          <label>
            {t('Editor.Host')}
            <input value={host} onChange={(e) => setHost(e.target.value)} />
          </label>
          <label>
            {t('Editor.RtspMain')}
            <input value={rtspMain} onChange={(e) => setRtspMain(e.target.value)} placeholder="rtsp://…" />
          </label>
          <label>
            {t('Editor.RtspSub')}
            <input value={rtspSub} onChange={(e) => setRtspSub(e.target.value)} placeholder="rtsp://…" />
          </label>
          <div className="two">
            <label>
              {t('Editor.HttpPort')}
              <input type="number" value={httpPort} onChange={(e) => setHttpPort(e.target.value)} />
            </label>
            <label>
              {t('Editor.OnvifPort')}
              <input
                type="number"
                value={onvifPort}
                onChange={(e) => setOnvifPort(e.target.value)}
                placeholder={t('Editor.Optional')}
              />
            </label>
          </div>
          <div className="two">
            <label>
              {t('Editor.Group')}
              <select value={groupId} onChange={(e) => setGroupId(e.target.value)}>
                <option value="">{t('Editor.NoGroup')}</option>
                {groups.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              {t('Editor.Quality')}
              <select value={quality} onChange={(e) => setQuality(e.target.value)}>
                {QUALITIES.map((q) => (
                  <option key={q} value={q}>
                    {q}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="two">
            <label>
              {t('Editor.Username')}
              <input
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="off"
                placeholder={editing && camera?.hasCredentials ? t('Editor.KeepBlank') : t('Editor.Optional')}
              />
            </label>
            <label>
              {t('Editor.Password')}
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="new-password"
                placeholder={editing && camera?.hasCredentials ? t('Editor.KeepBlank') : t('Editor.Optional')}
              />
            </label>
          </div>
          {error && <div className="err">{error}</div>}
          <div className="modal-actions">
            <button type="button" onClick={onClose}>
              {t('Editor.Cancel')}
            </button>
            <button type="submit" className="primary" disabled={busy}>
              {editing ? t('Editor.Save') : t('Editor.Add')}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
