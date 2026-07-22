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
        {can('ViewArchive') && (
        <NavItem to="/recordings" label={t('Nav.Recordings')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinejoin="round">
            <circle cx="12" cy="12" r="9" />
            <path d="M12 7v5l3 2" />
          </svg>
        </NavItem>
        )}
        <NavItem to="/settings" label={t('Nav.Settings')}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 8.9 19a1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 5 8.9a1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.9.3H9.5a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.9v.1a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
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
