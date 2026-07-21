import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth'
import { Shell } from './components/Shell'
import { Login } from './pages/Login'
import { Cameras } from './pages/Cameras'
import { Camera } from './pages/Camera'
import { Grid } from './pages/Grid'
import { Groups } from './pages/Groups'
import { System } from './pages/System'

// The whole app is client-routed: navigating between these never reloads the
// page, and — crucially — the Grid's <video> tiles are not torn down on route
// changes. The .NET host serves index.html for any of these paths (fallback).
export function App() {
  const { loading, user } = useAuth()

  if (loading) return <div className="spin" style={{ padding: 40 }} />

  return (
    <Routes>
      <Route path="/login" element={user ? <Navigate to="/cameras" replace /> : <Login />} />
      <Route element={user ? <Shell /> : <Navigate to="/login" replace />}>
        <Route index element={<Navigate to="/cameras" replace />} />
        <Route path="/cameras" element={<Cameras />} />
        <Route path="/camera/:id" element={<Camera />} />
        <Route path="/grid" element={<Grid />} />
        <Route path="/groups" element={<Groups />} />
        <Route path="/system" element={<System />} />
        <Route path="*" element={<Navigate to="/cameras" replace />} />
      </Route>
    </Routes>
  )
}
