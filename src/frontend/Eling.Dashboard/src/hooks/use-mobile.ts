import * as React from "react"
import { MOBILE_BREAKPOINT } from "@/lib/constants"

export function useIsMobile() {
  return React.useSyncExternalStore(
    (callback) => {
      const mql = window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT - 1}px)`)
      mql.addEventListener("change", callback)
      return () => mql.removeEventListener("change", callback)
    },
    () => window.innerWidth < MOBILE_BREAKPOINT,
    () => false
  )
}
