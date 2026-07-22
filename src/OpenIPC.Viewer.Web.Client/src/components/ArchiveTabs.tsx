import { NavLink } from 'react-router-dom'
import { useI18n } from '../i18n'

// Recordings and snapshots are two views of the same archive, so they share the
// one nav entry and switch here instead of taking a sidebar slot each — the nav
// is four items on purpose (it doubles as the phone tab bar).
export function ArchiveTabs() {
  const { t } = useI18n()
  return (
    <div className="tabs" style={{ marginBottom: 16 }}>
      <NavLink to="/recordings" className={({ isActive }) => (isActive ? 'active' : '')}>
        {t('Recordings.Tab')}
      </NavLink>
      <NavLink to="/snapshots" className={({ isActive }) => (isActive ? 'active' : '')}>
        {t('Snapshots.Title')}
      </NavLink>
    </div>
  )
}
