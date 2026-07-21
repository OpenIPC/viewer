import { useEffect, useMemo, useState } from 'react'
import { api, type CameraDto, type LayoutDto } from '../api'
import { useI18n } from '../i18n'
import { EmptyCell, LiveTile } from '../components/LiveTile'
import { ConfirmModal, TextPromptModal } from '../components/Modals'

// Grid size is the side N of an NxN grid (matches the backend/desktop model);
// cells = N*N. The UI labels each option by its cell count (1, 4, 9, 16, 25).
const SIDES = [1, 2, 3, 4, 5]


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

  // Pagination of the active layout. Clamped rather than corrected in state, so
  // a layout that shrank under us (another client edited it) still renders.
  const pageSize = active ? active.gridSize * active.gridSize : 1
  const pageCount = active ? Math.max(1, Math.ceil(active.tiles.length / pageSize)) : 1
  const safePage = Math.min(page, pageCount - 1)
  const pageStart = safePage * pageSize

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
        <>
          <div className={`videos n${pageSize}`}>
            {Array.from({ length: pageSize }, (_, i) => {
              const id = active.tiles[pageStart + i]
              const cam = id ? camById.get(id) : undefined
              return cam ? <LiveTile key={cam.id} camera={cam} /> : <EmptyCell key={`empty-${i}`} />
            })}
          </div>
          {pageCount > 1 && (
            <div className="pager">
              <button disabled={safePage === 0} title={t('Grid.PrevPage')} onClick={() => setPage(safePage - 1)}>
                ‹
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
                ›
              </button>
            </div>
          )}
        </>
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
