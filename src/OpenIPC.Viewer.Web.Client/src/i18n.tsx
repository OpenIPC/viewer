import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import { EN, RU } from './strings'

// Language from the `lang` cookie (kept from the Phase 20 server-rendered UI)
// else the browser's preference. Purely client-side now — switching is instant,
// with no reload.
type Lang = 'en' | 'ru'

function initialLang(): Lang {
  const cookie = document.cookie.match(/(?:^|;\s*)lang=(en|ru)\b/)
  if (cookie) return cookie[1] as Lang
  return navigator.language.toLowerCase().startsWith('ru') ? 'ru' : 'en'
}

type I18n = { lang: Lang; setLang: (l: Lang) => void; t: (key: string) => string }
const Ctx = createContext<I18n | null>(null)

export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(initialLang)

  const setLang = useCallback((l: Lang) => {
    document.cookie = `lang=${l}; path=/; max-age=${365 * 24 * 3600}; samesite=lax`
    setLangState(l)
  }, [])

  const value = useMemo<I18n>(() => {
    const table = lang === 'ru' ? RU : EN
    return { lang, setLang, t: (key: string) => table[key] ?? EN[key] ?? key }
  }, [lang, setLang])

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useI18n(): I18n {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error('useI18n outside I18nProvider')
  return ctx
}
