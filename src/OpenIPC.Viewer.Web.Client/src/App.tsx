import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth'
import { LivePool } from './live/LivePool'
import { Shell } from './components/Shell'
import { Login } from './pages/Login'
import { Cameras } from './pages/Cameras'
import { Camera } from './pages/Camera'
import { Grid } from './pages/Grid'
import { Discovery } from './pages/Discovery'
import { Users } from './pages/Users'
import { Recordings } from './pages/Recordings'
import { Groups } from './pages/Groups'
import { System } from './pages/System'

// The whole app is client-routed: navigating between these never reloads the
// page, and — crucially — live tiles are not torn down on route changes: the
// <video> elements live in <LivePool/>, which sits outside the router and
// therefore survives every navigation. The .NET host serves index.html for any
// of these paths (fallback).
export function App() {
  const { loading, user } = useAuth()

  if (loading) return <div className="spin" style={{ padding: 40 }} />

  return (
    <>
    <LivePool />
    <Routes>
      <Route path="/login" element={user ? <Navigate to="/cameras" replace /> : <Login />} />
      <Route element={user ? <Shell /> : <Navigate to="/login" replace />}>
        <Route index element={<Navigate to="/cameras" replace />} />
        <Route path="/cameras" element={<Cameras />} />
        <Route path="/camera/:id" element={<Camera />} />
        <Route path="/grid" element={<Grid />} />
        <Route path="/recordings" element={<Recordings />} />
        <Route path="/discovery" element={<Discovery />} />
        <Route path="/groups" element={<Groups />} />
        <Route path="/system" element={<System />} />
        <Route path="/users" element={<Users />} />
        <Route path="*" element={<Navigate to="/cameras" replace />} />
      </Route>
    </Routes>
    </>
  )
}
