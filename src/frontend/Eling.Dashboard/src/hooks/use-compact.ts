import * as React from "react"

// True when the viewport is below the given compact breakpoint: the sidebar
// stays visible as an icon strip but auto-collapses (ignoring the persisted
// expanded state) so the content column keeps enough room. The breakpoint can
// be raised per-page (e.g. the databases page) to give wide tables more room.
export function useIsCompact(breakpoint: number = 1024) {
  return React.useSyncExternalStore(
    (callback) => {
      const mql = window.matchMedia(`(max-width: ${breakpoint - 1}px)`)
      mql.addEventListener("change", callback)
      return () => mql.removeEventListener("change", callback)
    },
    () => window.innerWidth < breakpoint,
    () => false,
  )
}