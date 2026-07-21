// Thin typed wrapper over the Phase 20 /api/v1 surface. The SPA never handles a
// token: auth rides in the HttpOnly session cookie the login endpoint sets, so
// every request just needs `credentials: 'same-origin'` (default same-origin,
// but explicit for the Vite dev proxy).

export type CameraDto = {
  id: string
  name: string
  host: string
  httpPort: number
  onvifPort: number | null
  groupId: number | null
  groupName: string | null
  rtspMain: string
  rtspSub: string | null
  onvifEnabled: boolean
  chipModel: string | null
  firmwareVersion: string | null
  includedInGrid: boolean
  hasPtz: boolean
  ptzReady: boolean
  isMajestic: boolean
  hasAudioIn: boolean
  hasAudioOut: boolean
  hasCredentials: boolean
  streamQuality: string
  sortOrder: number
}

// Who the caller is and what they may do — the UI gates on `permissions`.
export type MeDto = {
  user: string
  roles: string[]
  permissions: string[]
  cameras: string[] | null
}

export type WebUserDto = {
  name: string
  role: string
  permissions: string[]
  cameras: string[] | null
}

export type GroupDto = { id: number; name: string; sortOrder: number }

export type PtzPresetDto = { token: string; name: string }

export type LayoutDto = {
  id: number
  name: string
  gridSize: number
  sortOrder: number
  tiles: string[] // ordered camera ids (position = index)
}

// A LAN device a scan turned up. `protocols` are the signals that found it
// (Onvif/Mdns/Majestic/Rtsp/Http) and `confidence` how much they agree.
export type DiscoveredDeviceDto = {
  host: string
  name: string | null
  model: string | null
  ports: number[]
  protocols: string[]
  confidence: 'Low' | 'Medium' | 'High'
  onvifPort: number | null
}

export type ScanDto = {
  id: string
  status: 'running' | 'done' | 'cancelled' | 'failed'
  progress: number
  error: string | null
  devices: DiscoveredDeviceDto[]
}

// What a probe learned about one candidate — the truth when ONVIF answered
// (onvifOk), a guessed RTSP URI otherwise.
export type CameraDraftDto = {
  host: string
  suggestedName: string
  rtspMain: string
  onvifOk: boolean
  manufacturer: string | null
  model: string | null
  firmwareVersion: string | null
  hasPtz: boolean
  hasAudioIn: boolean
  hasAudioOut: boolean
  error: string | null
}

export type DiscoveryAdd = {
  host: string
  name?: string
  httpPort?: number
  onvifPort?: number | null
  username?: string
  password?: string
  rtspMain?: string
  groupId?: number | null
}

export type CameraWrite = {
  name: string
  host: string
  httpPort?: number
  onvifPort?: number | null
  rtspMain: string
  rtspSub?: string | null
  groupId?: number | null
  username?: string | null
  password?: string | null
  streamQuality?: string
}

// A failed request carries the server's { error, details? } payload so callers
// can surface validation reasons (400 { error: "validation", details: [...] }).
export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    public details?: string[],
  ) {
    super(code)
  }
}

async function req<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? JSON.parse(text) : undefined
  if (!res.ok) {
    throw new ApiError(res.status, data?.error ?? `http_${res.status}`, data?.details)
  }
  return data as T
}

export const api = {
  version: () => req<{ product: string; version: string }>('GET', '/api/v1/version'),
  me: () => req<MeDto>('GET', '/api/v1/auth/me'),
  login: (user: string, password: string) =>
    req<MeDto & { expiresAt: string }>('POST', '/api/v1/auth/login', { user, password }),
  logout: () => req<void>('POST', '/api/v1/auth/logout'),

  cameras: () => req<CameraDto[]>('GET', '/api/v1/cameras'),

  // Web console accounts (Manage only). Passwords go in, never come back.
  users: () => req<{ available: boolean; users: WebUserDto[] }>('GET', '/api/v1/users'),
  createUser: (body: { name: string; password: string; permissions: string[]; cameras?: string[] | null }) =>
    req<WebUserDto>('POST', '/api/v1/users', body),
  updateUser: (
    name: string,
    body: { password?: string; permissions?: string[]; cameras?: string[] | null; allCameras?: boolean },
  ) => req<WebUserDto>('PUT', `/api/v1/users/${encodeURIComponent(name)}`, body),
  deleteUser: (name: string) => req<void>('DELETE', `/api/v1/users/${encodeURIComponent(name)}`),
  createCamera: (body: CameraWrite) => req<CameraDto>('POST', '/api/v1/cameras', body),
  updateCamera: (id: string, body: CameraWrite) =>
    req<CameraDto>('PUT', `/api/v1/cameras/${id}`, body),
  deleteCamera: (id: string) => req<void>('DELETE', `/api/v1/cameras/${id}`),

  // Discovery. A scan is a background job on the server: start it, then poll by
  // id until status leaves 'running' (results accumulate as sources report).
  startScan: (deepScan: boolean) => req<ScanDto>('POST', '/api/v1/discovery/scan', { deepScan }),
  getScan: (id: string) => req<ScanDto>('GET', `/api/v1/discovery/scan/${id}`),
  cancelScan: (id: string) => req<void>('DELETE', `/api/v1/discovery/scan/${id}`),
  probeDevice: (body: { host: string; onvifPort?: number | null; username?: string; password?: string }) =>
    req<CameraDraftDto>('POST', '/api/v1/discovery/probe', body),
  addDiscovered: (body: DiscoveryAdd) => req<CameraDto>('POST', '/api/v1/discovery/add', body),

  // A fresh still, straight from the camera (Majestic /image.jpg when available,
  // otherwise one ffmpeg pull). Used as an <img> src and as a download target,
  // so it's a URL rather than a fetch wrapper.
  snapshotUrl: (id: string) => `/api/v1/cameras/${id}/snapshot`,

  // PTZ. Movement is stateless on the server: each move carries a self-stop
  // timeout, so the caller must repeat it while a direction is held (see PtzPad)
  // and send stop on release. A camera that never gets the stop halts on its own.
  ptzMove: (id: string, v: { panX?: number; tiltY?: number; zoom?: number; timeoutMs?: number }) =>
    req<void>('POST', `/api/v1/cameras/${id}/ptz/move`, v),
  ptzStop: (id: string) => req<void>('POST', `/api/v1/cameras/${id}/ptz/stop`),
  ptzPresets: (id: string) => req<PtzPresetDto[]>('GET', `/api/v1/cameras/${id}/ptz/presets`),
  ptzSavePreset: (id: string, name: string) =>
    req<PtzPresetDto>('POST', `/api/v1/cameras/${id}/ptz/presets`, { name }),
  ptzGotoPreset: (id: string, token: string) =>
    req<void>('POST', `/api/v1/cameras/${id}/ptz/presets/${encodeURIComponent(token)}/goto`),
  ptzDeletePreset: (id: string, token: string) =>
    req<void>('DELETE', `/api/v1/cameras/${id}/ptz/presets/${encodeURIComponent(token)}`),

  system: () =>
    req<{ version: string; cameras: number; groups: number; sessions: number; streams: number }>(
      'GET',
      '/api/v1/system',
    ),

  // Backup/restore. Export is a plain download link (the browser handles the
  // file); import is a same-origin multipart POST answering with the counts.
  configExportUrl: '/api/v1/config/export',
  importConfig: async (file: File) => {
    const form = new FormData()
    form.append('file', file)
    const res = await fetch('/api/v1/config/import', {
      method: 'POST',
      body: form,
      credentials: 'same-origin',
    })
    const data = res.status === 204 ? undefined : await res.json().catch(() => undefined)
    if (!res.ok) throw new ApiError(res.status, data?.error ?? `http_${res.status}`, data?.details)
    return data as { added: number; updated: number }
  },
  revokeAllSessions: () => req<{ revoked: number }>('POST', '/api/v1/sessions/revoke-all'),

  groups: () => req<GroupDto[]>('GET', '/api/v1/groups'),
  createGroup: (name: string) => req<GroupDto>('POST', '/api/v1/groups', { name }),
  renameGroup: (id: number, name: string) => req<GroupDto>('PUT', `/api/v1/groups/${id}`, { name }),
  deleteGroup: (id: number) => req<void>('DELETE', `/api/v1/groups/${id}`),

  layouts: () => req<LayoutDto[]>('GET', '/api/v1/layouts'),
  createLayout: (name: string, gridSize: number) =>
    req<LayoutDto>('POST', '/api/v1/layouts', { name, gridSize }),
  updateLayout: (id: number, body: { name?: string; gridSize?: number }) =>
    req<LayoutDto>('PUT', `/api/v1/layouts/${id}`, body),
  deleteLayout: (id: number) => req<void>('DELETE', `/api/v1/layouts/${id}`),
  setLayoutTiles: (id: number, cameras: string[]) =>
    req<LayoutDto>('PUT', `/api/v1/layouts/${id}/tiles`, { cameras }),
}
