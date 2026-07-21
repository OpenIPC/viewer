import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The headless Kestrel host (`--server-only`) to proxy /api, /healthz and the
// live-video WebSocket at during dev. Override with OPENIPC_DEV_SERVER.
const target = process.env.OPENIPC_DEV_SERVER ?? 'http://localhost:8787'

// Paths the SPA does NOT own — proxied to the running .NET server in dev so the
// front-end can be developed live against a real backend. Everything else falls
// through to index.html (client-side routing).
const proxied = ['/api', '/healthz']

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // Emit straight into the Web project's wwwroot so Kestrel serves the built SPA.
  build: {
    outDir: '../OpenIPC.Viewer.Web/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: Object.fromEntries(
      proxied.map((path) => [
        path,
        { target, changeOrigin: false, ws: true },
      ]),
    ),
  },
})
