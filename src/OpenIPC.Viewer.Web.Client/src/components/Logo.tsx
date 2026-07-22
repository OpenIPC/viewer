// The OpenIPC aperture mark, ported from the desktop SplashAperture.axaml
// (same 100x100 geometry, same stroke widths) so the web console wears the same
// logo as the app icon. Inline SVG rather than the 512px PNG: it stays crisp at
// 18px in the sidebar, and `currentColor` lets the mark follow the theme.
export function Logo({ size = 18, className }: { size?: number; className?: string }) {
  return (
    <svg
      className={className}
      width={size}
      height={size}
      viewBox="0 0 100 100"
      fill="none"
      stroke="currentColor"
      strokeWidth={9}
      aria-hidden="true"
    >
      {/* Chevrons < > */}
      <path d="M33,29 L5,50 L33,71" strokeLinejoin="round" />
      <path d="M67,29 L95,50 L67,71" strokeLinejoin="round" />
      {/* Aperture caps ⌒ ⌣ */}
      <path d="M37,27 C43,12 57,12 63,27" strokeLinecap="round" />
      <path d="M37,73 C43,88 57,88 63,73" strokeLinecap="round" />
      {/* Pupil */}
      <circle cx="50" cy="50" r="13" fill="currentColor" stroke="none" />
    </svg>
  )
}
