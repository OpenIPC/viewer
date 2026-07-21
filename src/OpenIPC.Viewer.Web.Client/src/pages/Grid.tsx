import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { api, type CameraDto, type LayoutDto } from '../api'
import { useI18n } from '../i18n'
import { useLiveTile } from '../hooks/useLiveTile'
import { ConfirmModal, TextPromptModal } from '../components/Modals'

// Grid size is the side N of an NxN grid (matches the backend/desktop model);
// cells = N*N. The UI labels each option by its cell count (1, 4, 9, 16, 25).
const SIDES = [1, 2, 3, 4, 5]

// Collapse the raw useLiveTile status string into a small set of visual states:
// a dot colour + a terse label, plus whether to show the "still connecting"
// spinner (no frame yet).
type Kind = 'live' | 'connecting' | 'error' | 'offline'
function statusKind(status: string): { kind: Kind; label: string } {
  if (status === 'live' || status.includes('transcoded')) return { kind: 'live', label: 'live' }
  if (status === 'connecting' || status.startsWith('transcod')) return { kind: 'connecting', label: 'connecting' }
  if (status === 'ended') return { kind: 'offline', label: 'offline' }
  return { kind: 'error', label: status }
}

// A single live tile. Memoized and keyed by camera id in the grid so that
// changing the layout (size OR which saved layout is active) NEVER remounts a
// tile whose camera stays on screen — React matches it by key and only moves the
// DOM node. That is the whole app-like point: the <video>/WebSocket/ffmpeg
// session for a surviving camera is not torn down and restarted on a switch.
const Tile = memo(function Tile({ camera }: { camera: CameraDto }) {
  const [video, setVideo] = useState<HTMLVideoElement | null>(null)
  const cellRef = useRef<HTMLDivElement>(null)
  const status = useLiveTile(video, camera.id)
  const { kind, label } = statusKind(status)

  const toggleFullscreen = () => {
    const el = cellRef.current
    if (!el) return
    if (document.fullscreenElement) void document.exitFullscreen()
    else void el.requestFullscreen?.()
  }

  return (
    <div className="cell" ref={cellRef} onDoubleClick={toggleFullscreen}>
      <video ref={setVideo} autoPlay muted playsInline />
      {kind === 'connecting' && (
        <div className="loading">
          <span className="spinner" />
        </div>
      )}
      <span className="status">
        <span className={'dot ' + kind} />
        {label}
      </span>
      <span className="label">{camera.name}</span>
      <button className="expand" title="Fullscreen" onClick={toggleFullscreen}>
        ⤢
      </button>
    </div>
  )
})

function EmptyCell() {
  return (
    <div className="cell empty-slot">
      <div className="empty">—</div>
    </div>
  )
}

// The Live grid, driven by saved layouts (name + grid size + ordered camera
// tiles). Switching between layouts keeps shared cameras playing. An edit mode
// assigns a camera to each cell and changes the grid size.
export function Grid() {
  const { t } = useI18n()
  const [params, setParams] = useSearchParams()
  const only = params.get('only')

  const [cameras, setCameras] = useState<CameraDto[] | null>(null)
  const [layouts, setLayouts] = useState<LayoutDto[] | null>(null)
  const [activeId, setActiveId] = useState<number | null>(null)
  const [editing, setEditing] = useState(false)
  const [draftSize, setDraftSize] = useState(4)
  const [draftTiles, setDraftTiles] = useState<(string | null)[]>([])
  const [busy, setBusy] = useState(false)
  // Which dialog is open, if any (replaces native prompt/confirm).
  const [dialog, setDialog] = useState<'create' | 'rename' | 'delete' | null>(null)

  const camById = useMemo(() => {
    const m = new Map<string, CameraDto>()
    cameras?.forEach((c) => m.set(c.id, c))
    return m
  }, [cameras])

  useEffect(() => {
    let cancelled = false
    void Promise.all([api.cameras(), api.layouts()]).then(([cs, ls]) => {
      if (cancelled) return
      setCameras(cs)
      ls.sort((a, b) => a.sortOrder - b.sortOrder)
      setLayouts(ls)
      setActiveId(ls[0]?.id ?? null)
    })
    return () => {
      cancelled = true
    }
  }, [])

  const active = layouts?.find((l) => l.id === activeId) ?? null

  async function reloadLayouts(): Promise<LayoutDto[]> {
    const ls = await api.layouts()
    ls.sort((a, b) => a.sortOrder - b.sortOrder)
    setLayouts(ls)
    setActiveId((prev) => (prev != null && ls.some((l) => l.id === prev) ? prev : (ls[0]?.id ?? null)))
    return ls
  }

  // ---- single-camera mode (arriving from Cameras "▶ Live") ---------------
  if (only) {
    const cam = cameras?.find((c) => c.id === only)
    return (
      <div className="wrap" style={{ maxWidth: 'none' }}>
        <div className="toolbar">
          <h1 style={{ margin: 0, flex: 1 }}>{t('Grid.Single')}</h1>
          <button onClick={() => setParams({}, { replace: true })}>← {t('Grid.Title')}</button>
        </div>
        {cameras === null ? (
          <div className="spin">{t('Common.Loading')}</div>
        ) : !cam ? (
          <p className="muted">{t('Grid.Empty')}</p>
        ) : (
          <div className="videos n1">
            <Tile key={cam.id} camera={cam} />
          </div>
        )}
      </div>
    )
  }

  // ---- edit-mode actions --------------------------------------------------
  const startEdit = () => {
    if (!active) return
    setDraftSize(active.gridSize)
    setDraftTiles(Array.from({ length: active.gridSize * active.gridSize }, (_, i) => active.tiles[i] ?? null))
    setEditing(true)
  }
  const changeDraftSize = (n: number) => {
    setDraftSize(n)
    setDraftTiles((prev) => Array.from({ length: n * n }, (_, i) => prev[i] ?? null))
  }
  const setCell = (i: number, camId: string) =>
    setDraftTiles((prev) => {
      const next = [...prev]
      next[i] = camId || null
      return next
    })
  const save = async () => {
    if (!active) return
    setBusy(true)
    try {
      if (draftSize !== active.gridSize) await api.updateLayout(active.id, { gridSize: draftSize })
      // The editor only shows the first page (gridSize² cells). Desktop layouts
      // can hold more cameras than that (paginated), so preserve everything beyond
      // the edited page instead of truncating it. Tiles are a compact ordered
      // list — drop empty cells, keep order, then re-append the untouched tail.
      const edited = draftTiles.filter((x): x is string => !!x)
      const tail = active.tiles.slice(active.gridSize * active.gridSize)
      await api.setLayoutTiles(active.id, [...edited, ...tail])
      await reloadLayouts()
      setEditing(false)
    } finally {
      setBusy(false)
    }
  }

  const createLayout = async (name: string) => {
    setDialog(null)
    const created = await api.createLayout(name, 2)
    await reloadLayouts()
    setActiveId(created.id)
  }
  const renameLayout = async (name: string) => {
    setDialog(null)
    if (!active || name === active.name) return
    await api.updateLayout(active.id, { name })
    await reloadLayouts()
  }
  const deleteLayout = async () => {
    setDialog(null)
    if (!active) return
    await api.deleteLayout(active.id)
    setEditing(false)
    setActiveId(null)
    await reloadLayouts()
  }

  const loading = cameras === null || layouts === null

  return (
    <div className="wrap" style={{ maxWidth: 'none' }}>
      <div className="toolbar" style={{ flexWrap: 'wrap' }}>
        <h1 style={{ margin: 0 }}>{t('Grid.Title')}</h1>
        <div className="tabs" style={{ flex: 1 }}>
          {layouts?.map((l) => (
            <button
              key={l.id}
              className={l.id === activeId ? 'active' : ''}
              onClick={() => {
                setEditing(false)
                setActiveId(l.id)
              }}
            >
              {l.name}
            </button>
          ))}
          {!editing && <button onClick={() => setDialog('create')}>{t('Grid.NewLayout')}</button>}
        </div>
        {active && !editing && <button onClick={startEdit}>{t('Grid.Edit')}</button>}
        {editing && (
          <div className="row">
            <button onClick={() => setDialog('rename')}>{t('Grid.Rename')}</button>
            <button className="danger" onClick={() => setDialog('delete')}>
              {t('Grid.DeleteLayout')}
            </button>
            <span style={{ width: 8 }} />
            <button onClick={() => setEditing(false)}>{t('Common.Cancel')}</button>
            <button className="primary" onClick={save} disabled={busy}>
              {t('Common.Save')}
            </button>
          </div>
        )}
      </div>

      {editing && (
        <div className="tabs" style={{ marginBottom: 8 }}>
          <span className="muted" style={{ marginRight: 8 }}>
            {t('Grid.Size')}:
          </span>
          {SIDES.map((n) => (
            <button key={n} className={draftSize === n ? 'active' : ''} onClick={() => changeDraftSize(n)}>
              {n * n}
            </button>
          ))}
        </div>
      )}

      {loading ? (
        <div className="spin">{t('Common.Loading')}</div>
      ) : !active ? (
        <p className="muted">{t('Grid.NoLayouts')}</p>
      ) : editing ? (
        <div className={`videos n${draftSize * draftSize}`}>
          {draftTiles.map((camId, i) => (
            <div className="cell empty-slot" key={i} style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 12 }}>
              <select value={camId ?? ''} onChange={(e) => setCell(i, e.target.value)} style={{ maxWidth: '90%' }}>
                <option value="">{t('Grid.EmptyCell')}</option>
                {cameras!.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            </div>
          ))}
        </div>
      ) : (
        <div className={`videos n${active.gridSize * active.gridSize}`}>
          {Array.from({ length: active.gridSize * active.gridSize }, (_, i) => {
            const cam = active.tiles[i] ? camById.get(active.tiles[i]) : undefined
            return cam ? <Tile key={cam.id} camera={cam} /> : <EmptyCell key={`empty-${i}`} />
          })}
        </div>
      )}

      {dialog === 'create' && (
        <TextPromptModal
          title={t('Grid.NewLayoutTitle')}
          label={t('Grid.NewLayoutName')}
          submitLabel={t('Common.Save')}
          onSubmit={createLayout}
          onCancel={() => setDialog(null)}
        />
      )}
      {dialog === 'rename' && active && (
        <TextPromptModal
          title={t('Grid.Rename')}
          label={t('Grid.NewLayoutName')}
          initial={active.name}
          submitLabel={t('Common.Save')}
          onSubmit={renameLayout}
          onCancel={() => setDialog(null)}
        />
      )}
      {dialog === 'delete' && active && (
        <ConfirmModal
          title={t('Grid.DeleteLayout')}
          message={t('Grid.DeleteLayoutConfirm')}
          confirmLabel={t('Cameras.Delete')}
          danger
          onConfirm={deleteLayout}
          onCancel={() => setDialog(null)}
        />
      )}
    </div>
  )
}
