import type { ReactNode } from 'react'
import { NavLink, Navigate, Outlet } from 'react-router-dom'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'

// Everything you configure rather than watch, behind one nav entry.
//
// The sidebar had grown to seven items — too many for the mobile tab strip, and
// it mixed daily viewing (grid, cameras, archive) with things touched once a
// month. Groups, users and server status live here now; each tab keeps its own
// URL, so links and the back button still work.
export function Settings() {
  const { t } = useI18n()
  const { can } = useAuth()

  return (
    <div className="wrap" style={{ maxWidth: 1000 }}>
      <h1>{t('Nav.Settings')}</h1>
      <div className="tabs settings-tabs">
        <NavLink to="/settings/groups" className={({ isActive }) => (isActive ? 'active' : '')}>
          {t('Nav.Groups')}
        </NavLink>
        {can('Manage') && (
          <NavLink to="/settings/users" className={({ isActive }) => (isActive ? 'active' : '')}>
            {t('Nav.Users')}
          </NavLink>
        )}
        <NavLink to="/settings/system" className={({ isActive }) => (isActive ? 'active' : '')}>
          {t('Nav.System')}
        </NavLink>
      </div>
      <Outlet />
    </div>
  )
}

// Landing on /settings alone should show something rather than an empty frame.
export function SettingsIndex() {
  return <Navigate to="/settings/system" replace />
}

// A hidden tab is not a closed door: typing the URL used to load a page whose
// first request 403s, leaving it stuck on "Loading…". Send those callers to a
// tab they can actually use. (The API refuses them regardless — this is about
// not showing a broken screen.)
export function RequireManage({ children }: { children: ReactNode }) {
  const { can } = useAuth()
  return can('Manage') ? <>{children}</> : <Navigate to="/settings/system" replace />
}
