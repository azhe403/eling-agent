import * as React from "react"

/**
 * Viewport state for an arbitrary media query, same shape as `useIsMobile`:
 * a useSyncExternalStore subscription over matchMedia, with a server snapshot
 * of false. Safe on this page because the component tree below the codebase
 * page's Suspense boundary is client-rendered (useSearchParams CSR bailout),
 * so the server snapshot never has to match client markup.
 */
export function useMediaQuery(query: string) {
  return React.useSyncExternalStore(
    (callback) => {
      const mql = window.matchMedia(query)
      mql.addEventListener("change", callback)
      return () => mql.removeEventListener("change", callback)
    },
    () => window.matchMedia(query).matches,
    () => false
  )
}
