import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, type CameraDto, type LayoutDto } from '../api'
import { useI18n } from '../i18n'
import { Icon } from '../components/Icon'
import { useAuth } from '../auth'
import { EmptyCell, LiveTile } from '../components/LiveTile'
import { StillTile } from '../components/StillTile'
import { ConfirmModal, TextPromptModal } from '../components/Modals'

// Grid size is the side N of an NxN grid (matches the backend/desktop model);
// cells = N*N. The UI labels each option by its cell count (1, 4, 9, 16, 25).
const SIDES = [1, 2, 3, 4, 5]

// Refresh rates for stills mode, same ladder as the desktop grid.
const STILL_INTERVALS = [2, 5, 10, 30, 60]
// How long a kiosk wall holds a page before flipping to the next one.
const ROTATE_INTERVALS = [10, 30, 60]

// Wall preferences (stills, kiosk rotation) are per viewer, not per install:
// the same layout is watched on a control-room screen and on someone's laptop,
// and only one of them wants a full-screen wall of stills. So they live in the
// browser, unlike layouts, which are shared server state.
function loadPref<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem('openipc.grid.' + key)
    return raw === null ? fallback : (JSON.parse(raw) as T)
  } catch {
    return fallback
  }
}
function savePref(key: string, value: unknown) {
  try {
    localStorage.setItem('openipc.grid.' + key, JSON.stringify(value))
  } catch { /* private mode / quota — the preference just won't stick */ }
}


// The Live grid, driven by saved layouts (name + grid size + ordered camera
// tiles). Switching between layouts keeps shared cameras playing. An edit mode
// assigns a camera to each cell and changes the grid size.
//
// A layout may hold more cameras than the grid has cells (the desktop's 82-camera
// "Default" is one), so the tile list is paginated exactly like the desktop head:
// one page = gridSize² cells, page count = ceil(tiles / pageSize). Only the
// current page gets live tiles — flipping pages tears the old sockets down.
export function Grid() {
  const { t } = useI18n()
  const { user, can } = useAuth()
  const [cameras, setCameras] = useState<CameraDto[] | null>(null)
  const [layouts, setLayouts] = useState<LayoutDto[] | null>(null)
  const [activeId, setActiveId] = useState<number | null>(null)
  const [page, setPage] = useState(0)
  const [editing, setEditing] = useState(false)
  const [draftSize, setDraftSize] = useState(4)
  const [draftTiles, setDraftTiles] = useState<(string | null)[]>([])
  const [busy, setBusy] = useState(false)
  // Which dialog is open, if any (replaces native prompt/confirm).
  const [dialog, setDialog] = useState<'create' | 'rename' | 'delete' | null>(null)
  // Wall modes: periodic stills instead of live streams, and a chrome-free
  // fullscreen wall that can cycle through the layout's pages on its own.
  const [stills, setStills] = useState(() => loadPref('stills', false))
  const [stillInterval, setStillInterval] = useState(() => loadPref('stillInterval', 5))
  const [rotate, setRotate] = useState(() => loadPref('rotate', false))
  const [rotateInterval, setRotateInterval] = useState(() => loadPref('rotateInterval', 30))
  const [kiosk, setKiosk] = useState(false)
  const [stage, setStage] = useState<HTMLDivElement | null>(null)

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

  useEffect(() => savePref('stills', stills), [stills])
  useEffect(() => savePref('stillInterval', stillInterval), [stillInterval])
  useEffect(() => savePref('rotate', rotate), [rotate])
  useEffect(() => savePref('rotateInterval', rotateInterval), [rotateInterval])

  // Kiosk is a CSS mode first and a fullscreen request second. The Fullscreen
  // API is refused outright in an embedded/iframed page, and a wall shown that
  // way (a kiosk browser, a dashboard frame) still wants the chrome gone — so
  // the mode never depends on the request being granted.
  const enterKiosk = () => {
    setKiosk(true)
    // Refusal comes back either way depending on the browser — a rejected
    // promise, or a synchronous throw that would take the click handler (and
    // the mode with it) down. Both mean the same thing: CSS-only kiosk.
    try {
      void stage?.requestFullscreen?.()?.catch(() => { /* CSS-only kiosk, then */ })
    } catch { /* CSS-only kiosk, then */ }
  }
  const exitKiosk = useCallback(() => {
    setKiosk(false)
    if (document.fullscreenElement) void document.exitFullscreen()
  }, [])

  // Leaving fullscreen by the browser's own means (Esc, F11, the tile that went
  // fullscreen on its own) must drop the mode too, and Esc has to work in the
  // CSS-only case where the browser has no fullscreen to leave.
  useEffect(() => {
    if (!kiosk) return
    const onFullscreenChange = () => {
      if (!document.fullscreenElement) setKiosk(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !document.fullscreenElement) setKiosk(false)
    }
    document.addEventListener('fullscreenchange', onFullscreenChange)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('fullscreenchange', onFullscreenChange)
      document.removeEventListener('keydown', onKey)
    }
  }, [kiosk])

  const active = layouts?.find((l) => l.id === activeId) ?? null

  // A layout is global, but a restricted user may only see part of it. Drop the
  // tiles they can't see before paginating — otherwise someone with two cameras
  // pages through ten screens of empty cells.
  const restricted = user?.cameras != null
  const tiles = active
    ? restricted
      ? active.tiles.filter((id) => camById.has(id))
      : active.tiles
    : []

  // Pagination of the active layout. Clamped rather than corrected in state, so
  // a layout that shrank under us (another client edited it) still renders.
  const pageSize = active ? active.gridSize * active.gridSize : 1
  const pageCount = active ? Math.max(1, Math.ceil(tiles.length / pageSize)) : 1
  const safePage = Math.min(page, pageCount - 1)
  const pageStart = safePage * pageSize

  // Auto-flip through the pages of a kiosk wall. Only in kiosk: someone working
  // in the console wants the page they chose to stay put.
  useEffect(() => {
    if (!kiosk || !rotate || pageCount < 2 || editing) return
    const timer = window.setInterval(
      () => setPage((p) => (p + 1) % pageCount),
      Math.max(5, rotateInterval) * 1000,
    )
    return () => window.clearInterval(timer)
  }, [kiosk, rotate, rotateInterval, pageCount, editing])

  async function reloadLayouts(): Promise<LayoutDto[]> {
    const ls = await api.layouts()
    ls.sort((a, b) => a.sortOrder - b.sortOrder)
    setLayouts(ls)
    setActiveId((prev) => (prev != null && ls.some((l) => l.id === prev) ? prev : (ls[0]?.id ?? null)))
    return ls
  }

  // ---- edit-mode actions --------------------------------------------------
  const startEdit = () => {
    if (!active) return
    setDraftSize(active.gridSize)
    setDraftTiles(Array.from({ length: pageSize }, (_, i) => active.tiles[pageStart + i] ?? null))
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
      // The editor shows one page; everything outside it must survive untouched,
      // so splice the edited cells back between the pages before and after.
      // Tiles are a compact ordered list (no holes), so emptied cells are dropped
      // and the tail slides up — that's the model, not a bug.
      const edited = draftTiles.filter((x): x is string => !!x)
      const head = active.tiles.slice(0, pageStart)
      const tail = active.tiles.slice(pageStart + pageSize)
      await api.setLayoutTiles(active.id, [...head, ...edited, ...tail])
      await reloadLayouts()
      setEditing(false)
      // A different grid size re-cuts every page boundary; the old page index
      // would land somewhere arbitrary, so go back to the first page.
      if (draftSize !== active.gridSize) setPage(0)
    } finally {
      setBusy(false)
    }
  }

  const createLayout = async (name: string) => {
    setDialog(null)
    const created = await api.createLayout(name, 2)
    await reloadLayouts()
    setActiveId(created.id)
    setPage(0)
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
    setPage(0)
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
                setPage(0)
              }}
            >
              {l.name}
            </button>
          ))}
          {!editing && can('Manage') && !restricted && (
            <button className="row" onClick={() => setDialog('create')}>
              <Icon name="plus" size={13} /> {t('Grid.NewLayout')}
            </button>
          )}
        </div>
        {active && !editing && (
          <div className="row">
            <button
              className={stills ? 'active' : ''}
              title={t('Grid.StillsHint')}
              onClick={() => setStills(!stills)}
            >
              {t('Grid.Stills')}
            </button>
            {stills && (
              <select
                value={stillInterval}
                title={t('Grid.StillsInterval')}
                onChange={(e) => setStillInterval(Number(e.target.value))}
              >
                {STILL_INTERVALS.map((s) => (
                  <option key={s} value={s}>
                    {s} {t('Common.Seconds')}
                  </option>
                ))}
              </select>
            )}
            <button className="row" title={t('Grid.KioskHint')} onClick={enterKiosk}>
              <Icon name="maximize" size={13} /> {t('Grid.Kiosk')}
            </button>
          </div>
        )}
        {active && !editing && can('Manage') && !restricted && (
          <button onClick={startEdit}>{t('Grid.Edit')}</button>
        )}
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
        <>
        {pageCount > 1 && (
          <p className="muted" style={{ margin: '0 0 8px', fontSize: 12 }}>
            {t('Grid.EditingPage')} {safePage + 1} / {pageCount}
          </p>
        )}
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
        </>
      ) : (
        <div className={'stage' + (kiosk ? ' kiosk' : '')} ref={setStage}>
          {kiosk && (
            <div className="kiosk-bar">
              {pageCount > 1 && (
                <>
                  <label className="row">
                    <input type="checkbox" checked={rotate} onChange={(e) => setRotate(e.target.checked)} />
                    {t('Grid.Rotate')}
                  </label>
                  {rotate && (
                    <select value={rotateInterval} onChange={(e) => setRotateInterval(Number(e.target.value))}>
                      {ROTATE_INTERVALS.map((s) => (
                        <option key={s} value={s}>
                          {s} {t('Common.Seconds')}
                        </option>
                      ))}
                    </select>
                  )}
                </>
              )}
              <button onClick={exitKiosk}>{t('Grid.KioskExit')}</button>
            </div>
          )}
          <div className={`videos n${pageSize}`}>
            {Array.from({ length: pageSize }, (_, i) => {
              const id = tiles[pageStart + i]
              const cam = id ? camById.get(id) : undefined
              if (!cam) return <EmptyCell key={`empty-${i}`} />
              return stills ? (
                <StillTile key={cam.id} camera={cam} intervalSeconds={stillInterval} />
              ) : (
                <LiveTile key={cam.id} camera={cam} />
              )
            })}
          </div>
          {pageCount > 1 && (
            <div className="pager">
              <button disabled={safePage === 0} title={t('Grid.PrevPage')} onClick={() => setPage(safePage - 1)}>
                <Icon name="chevronLeft" size={14} />
              </button>
              {Array.from({ length: pageCount }, (_, i) => (
                <button
                  key={i}
                  className={i === safePage ? 'active' : ''}
                  onClick={() => setPage(i)}
                >
                  {i + 1}
                </button>
              ))}
              <button
                disabled={safePage + 1 >= pageCount}
                title={t('Grid.NextPage')}
                onClick={() => setPage(safePage + 1)}
              >
                <Icon name="chevronRight" size={14} />
              </button>
            </div>
          )}
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
