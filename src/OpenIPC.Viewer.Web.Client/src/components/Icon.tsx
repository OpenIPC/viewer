import type { ReactNode } from 'react'

// The SPA's icon set: inline SVG in the Lucide style the desktop app already
// uses (24-unit box, no fill, 2-unit round strokes). Inline rather than a font
// or an icon package so there's nothing to load and nothing to keep in sync.
//
// Never emoji: those render in a system font that differs per platform, ignore
// the theme colour, and don't scale with the button they sit in. These inherit
// `currentColor` and take their size from the caller.
const PATHS: Record<string, ReactNode> = {
  plus: <path d="M12 5v14M5 12h14" />,
  minus: <path d="M5 12h14" />,
  x: <path d="M6 6l12 12M18 6L6 18" />,
  record: <circle cx="12" cy="12" r="6" fill="currentColor" stroke="none" />,
  back: <path d="M19 12H5M12 19l-7-7 7-7" />,
  search: (
    <>
      <circle cx="11" cy="11" r="7" />
      <path d="M20 20l-3.5-3.5" />
    </>
  ),
  play: <path d="M6 4l14 8-14 8V4z" fill="currentColor" stroke="none" />,
  stop: <rect x="6" y="6" width="12" height="12" rx="1.5" fill="currentColor" stroke="none" />,
  download: <path d="M12 3v12M7 11l5 5 5-5M4 21h16" />,
  camera: (
    <>
      <path d="M4 8h3l2-2h6l2 2h3v11H4V8z" />
      <circle cx="12" cy="13" r="3.5" />
    </>
  ),
  maximize: <path d="M8 3H3v5M16 3h5v5M8 21H3v-5M16 21h5v-5" />,
  volumeOn: (
    <>
      <path d="M11 5L6 9H3v6h3l5 4V5z" />
      <path d="M16 9a4 4 0 0 1 0 6M19 6a8 8 0 0 1 0 12" />
    </>
  ),
  volumeOff: (
    <>
      <path d="M11 5L6 9H3v6h3l5 4V5z" />
      <path d="M17 9l4 6M21 9l-4 6" />
    </>
  ),
  chevronLeft: <path d="M15 5l-7 7 7 7" />,
  chevronRight: <path d="M9 5l7 7-7 7" />,
  chevronUp: <path d="M5 15l7-7 7 7" />,
  chevronDown: <path d="M5 9l7 7 7-7" />,
  arrowUpLeft: <path d="M17 17L7 7M7 7v7M7 7h7" />,
  arrowUpRight: <path d="M7 17L17 7M17 7v7M17 7h-7" />,
  arrowDownLeft: <path d="M17 7L7 17M7 17v-7M7 17h7" />,
  arrowDownRight: <path d="M7 7l10 10M17 17v-7M17 17h-7" />,
}

export type IconName = keyof typeof PATHS

export function Icon({ name, size = 16 }: { name: IconName; size?: number }) {
  return (
    <svg
      className="icon"
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {PATHS[name]}
    </svg>
  )
}
