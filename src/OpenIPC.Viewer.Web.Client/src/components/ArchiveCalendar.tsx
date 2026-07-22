import { useMemo } from 'react'
import type { CalendarPointDto } from '../api'
import { useI18n } from '../i18n'
import { Icon } from './Icon'

// A month grid of the archive: which days have footage, and how much.
//
// Days are computed from the browser's own clock rather than the server's,
// because "which day is this recording on" is a question about the viewer's
// time zone — a phone in another country must not see everything shifted.
// Grouping local Date objects also gets DST right for free.
export function ArchiveCalendar({
  month, points, selected, onPickMonth, onPickDay,
}: {
  month: Date
  points: CalendarPointDto[]
  selected: string | null
  onPickMonth: (next: Date) => void
  onPickDay: (dayKey: string | null) => void
}) {
  const { t, lang } = useI18n()

  const byDay = useMemo(() => {
    const map = new Map<string, { count: number; bytes: number }>()
    for (const p of points) {
      const key = dayKey(new Date(p.startedAt))
      const cur = map.get(key) ?? { count: 0, bytes: 0 }
      map.set(key, { count: cur.count + 1, bytes: cur.bytes + p.sizeBytes })
    }
    return map
  }, [points])

  const busiest = useMemo(
    () => Math.max(1, ...[...byDay.values()].map((d) => d.count)),
    [byDay],
  )

  const cells = useMemo(() => {
    const first = new Date(month.getFullYear(), month.getMonth(), 1)
    // Monday-first, like the desktop calendar and both UI languages expect.
    const lead = (first.getDay() + 6) % 7
    const daysInMonth = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate()
    const out: (Date | null)[] = Array.from({ length: lead }, () => null)
    for (let d = 1; d <= daysInMonth; d++) out.push(new Date(month.getFullYear(), month.getMonth(), d))
    return out
  }, [month])

  const monthLabel = month.toLocaleDateString(lang === 'ru' ? 'ru-RU' : 'en-GB', {
    month: 'long',
    year: 'numeric',
  })
  const shift = (delta: number) => onPickMonth(new Date(month.getFullYear(), month.getMonth() + delta, 1))

  return (
    <div className="calendar">
      <div className="calendar-head">
        <button title={t('Archive.PrevMonth')} onClick={() => shift(-1)}>
          <Icon name="chevronLeft" size={14} />
        </button>
        <span className="calendar-month">{monthLabel}</span>
        <button title={t('Archive.NextMonth')} onClick={() => shift(1)}>
          <Icon name="chevronRight" size={14} />
        </button>
        {selected && (
          <button className="row" onClick={() => onPickDay(null)}>
            <Icon name="x" size={13} /> {t('Archive.ClearDay')}
          </button>
        )}
      </div>

      <div className="calendar-grid">
        {weekdayLabels(lang).map((w) => (
          <span key={w} className="calendar-weekday">
            {w}
          </span>
        ))}
        {cells.map((date, i) => {
          if (!date) return <span key={`pad-${i}`} />
          const key = dayKey(date)
          const activity = byDay.get(key)
          const isSelected = key === selected
          return (
            <button
              key={key}
              className={
                'calendar-day' +
                (activity ? ' has-activity' : '') +
                (isSelected ? ' selected' : '') +
                (isToday(date) ? ' today' : '')
              }
              // Shading tracks how busy the day was against the busiest one,
              // the same normalisation the desktop calendar uses.
              style={activity ? { opacity: 0.45 + 0.55 * (activity.count / busiest) } : undefined}
              disabled={!activity}
              title={activity ? `${activity.count} · ${(activity.bytes / (1024 * 1024)).toFixed(0)} MB` : undefined}
              onClick={() => onPickDay(isSelected ? null : key)}
            >
              {date.getDate()}
              {activity && <span className="calendar-dot" />}
            </button>
          )
        })}
      </div>
    </div>
  )
}

// Local-date key (YYYY-MM-DD) — never toISOString(), which would shift the day
// for anyone east or west of UTC.
export function dayKey(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

// The [start, end) UTC instants of a local day, for the server's range filter.
export function dayRange(key: string): { from: string; to: string } {
  const [y, m, d] = key.split('-').map(Number)
  const start = new Date(y, m - 1, d)
  const end = new Date(y, m - 1, d + 1)
  return { from: start.toISOString(), to: end.toISOString() }
}

export function monthRange(month: Date): { from: string; to: string } {
  const start = new Date(month.getFullYear(), month.getMonth(), 1)
  const end = new Date(month.getFullYear(), month.getMonth() + 1, 1)
  return { from: start.toISOString(), to: end.toISOString() }
}

function isToday(date: Date): boolean {
  return dayKey(date) === dayKey(new Date())
}

function weekdayLabels(lang: string): string[] {
  return lang === 'ru'
    ? ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'вс']
    : ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
}
