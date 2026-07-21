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
  isMajestic: boolean
  hasAudioIn: boolean
  hasAudioOut: boolean
  hasCredentials: boolean
  streamQuality: string
  sortOrder: number
}

export type GroupDto = { id: number; name: string; sortOrder: number }

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
  me: () => req<{ user: string; roles: string[] }>('GET', '/api/v1/auth/me'),
  login: (user: string, password: string) =>
    req<{ user: string; roles: string[]; expiresAt: string }>('POST', '/api/v1/auth/login', {
      user,
      password,
    }),
  logout: () => req<void>('POST', '/api/v1/auth/logout'),

  cameras: () => req<CameraDto[]>('GET', '/api/v1/cameras'),
  createCamera: (body: CameraWrite) => req<CameraDto>('POST', '/api/v1/cameras', body),
  updateCamera: (id: string, body: CameraWrite) =>
    req<CameraDto>('PUT', `/api/v1/cameras/${id}`, body),
  deleteCamera: (id: string) => req<void>('DELETE', `/api/v1/cameras/${id}`),

  system: () =>
    req<{ version: string; cameras: number; groups: number; sessions: number; streams: number }>(
      'GET',
      '/api/v1/system',
    ),

  groups: () => req<GroupDto[]>('GET', '/api/v1/groups'),
  createGroup: (name: string) => req<GroupDto>('POST', '/api/v1/groups', { name }),
  renameGroup: (id: number, name: string) => req<GroupDto>('PUT', `/api/v1/groups/${id}`, { name }),
  deleteGroup: (id: number) => req<void>('DELETE', `/api/v1/groups/${id}`),
}
