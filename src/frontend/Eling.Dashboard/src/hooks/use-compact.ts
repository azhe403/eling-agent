import * as React from "react"

import { COMPACT_BREAKPOINT } from "@/lib/constants"

// True when the viewport is below the given compact breakpoint: the sidebar
// stays visible as an icon strip but auto-collapses (ignoring the persisted
// expanded state) so the content column keeps enough room. The breakpoint is
// the app-wide COMPACT_BREAKPOINT, so a page never collapses the sidebar on
// its own account — that would override the user's saved choice.
export function useIsCompact(breakpoint: number = COMPACT_BREAKPOINT) {
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