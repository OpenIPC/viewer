import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth'
import { useI18n } from '../i18n'
import type { ReactNode } from 'react'

// The persistent app frame: fixed left sidebar + routed content. Markup/skin
// ported from the Phase 20 NavSidebar.razor; nav uses client-side routing so the
// content swaps without a page reload.
export function Shell() {
  const { user, can, logout } = useAuth()
  const { t, setLang } = useI18n()
  const navigate = useNavigate()

  const onLogout = async () => {
    await logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="shell">
      <nav className="sidebar">
        <div className="brand">
          <span className="dot" /> OpenIPC
        </div>

        <NavItem to="/grid" label={t('Nav.Grid')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinejoin="round">
            <rect x="3" y="3" width="7" height="7" rx="1" />
            <rect x="14" y="3" width="7" height="7" rx="1" />
            <rect x="3" y="14" width="7" height="7" rx="1" />
            <rect x="14" y="14" width="7" height="7" rx="1" />
          </svg>
        </NavItem>
        <NavItem to="/cameras" label={t('Nav.Cameras')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinejoin="round">
            <path d="M23 7l-7 5 7 5V7z" />
            <rect x="1" y="5" width="15" height="14" rx="2" />
          </svg>
        </NavItem>
        {can('Manage') && (
        <NavItem to="/discovery" label={t('Nav.Discovery')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round">
            <circle cx="12" cy="12" r="2" />
            <path d="M16.2 7.8a6 6 0 0 1 0 8.4M7.8 16.2a6 6 0 0 1 0-8.4" />
            <path d="M19.1 4.9a10 10 0 0 1 0 14.2M4.9 19.1a10 10 0 0 1 0-14.2" />
          </svg>
        </NavItem>
        )}
        <NavItem to="/groups" label={t('Nav.Groups')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinejoin="round">
            <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
            <circle cx="9" cy="7" r="4" />
            <path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" />
          </svg>
        </NavItem>
        {can('Manage') && (
        <NavItem to="/users" label={t('Nav.Users')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinejoin="round">
            <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
            <circle cx="12" cy="7" r="4" />
          </svg>
        </NavItem>
        )}
        <NavItem to="/system" label={t('Nav.System')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
            <line x1="4" y1="6" x2="20" y2="6" />
            <line x1="4" y1="12" x2="20" y2="12" />
            <line x1="4" y1="18" x2="20" y2="18" />
            <circle cx="9" cy="6" r="2" fill="var(--bg0)" />
            <circle cx="15" cy="12" r="2" fill="var(--bg0)" />
            <circle cx="9" cy="18" r="2" fill="var(--bg0)" />
          </svg>
        </NavItem>

        <div className="spacer" />

        <div className="foot">
          <span className="who">{user?.user}</span>
          <span>
            <a onClick={() => setLang('en')} style={{ cursor: 'pointer' }}>
              EN
            </a>{' '}
            ·{' '}
            <a onClick={() => setLang('ru')} style={{ cursor: 'pointer' }}>
              RU
            </a>
          </span>
          <button type="button" style={{ width: '100%' }} onClick={onLogout}>
            {t('Nav.SignOut')}
          </button>
        </div>
      </nav>
      <main>
        <Outlet />
      </main>
    </div>
  )
}

function NavItem({ to, label, children }: { to: string; label: string; children: ReactNode }) {
  return (
    <NavLink to={to} className={({ isActive }) => 'navitem' + (isActive ? ' active' : '')}>
      {children}
      {label}
    </NavLink>
  )
}
