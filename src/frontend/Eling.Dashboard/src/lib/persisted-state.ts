/**
 * Per-page persisted state — the one canonical pattern for filter/scope state
 * on a dashboard page: the URL query is the source of truth (reload,
 * back/forward and shared links land on the same state), and localStorage
 * remembers the last position so a bare revisit resumes where the user left
 * off. The URL remains authoritative the moment it carries any state —
 * saved position is only used to fill a completely bare address.
 *
 * Conventions (do not reinvent per page):
 * - Absent param == the default value; the param only appears in the URL
 *   when the user picked a non-default (keeps addresses clean).
 * - localStorage keys are `eling:<page>:<param>`; a value equal to the
 *   default clears the key.
 * - Writes use `router.replace` with `scroll: false` — state changes are not
 *   navigation steps; the address bar is a reflection, not history.
 * - Read order on mount: URL param (if valid) > saved position (bare URL
 *   only) > default. Invalid stored values fall back to the default silently.
 * - Pages keep owning semantics (what the params mean, which values are
 *   allowed); this module only owns persistence mechanics.
 */

import { useCallback, useEffect, useRef, useState } from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"

const STORAGE_PREFIX = "eling:"

function storageKey(page: string, param: string): string {
  return `${STORAGE_PREFIX}${page}:${param}`
}

export function savePageParam(page: string, param: string, value: string | null): void {
  try {
    if (value === null || value === "") {
      localStorage.removeItem(storageKey(page, param))
    } else {
      localStorage.setItem(storageKey(page, param), value)
    }
  } catch {
    // Storage unavailable (private mode, quota): the position is simply not
    // remembered, which costs nothing but the convenience.
  }
}

export function loadPageParam(page: string, param: string): string | null {
  try {
    return localStorage.getItem(storageKey(page, param))
  } catch {
    return null
  }
}

export type PersistedStateConfig<T extends string> = {
  /** Page identity, e.g. "skills". Used for the localStorage key. */
  readonly page: string
  /** URL query parameter name, e.g. "q". */
  readonly param: string
  /** The value that means "unset" (the param is omitted from the URL then). */
  readonly defaultValue: T
  /** Allowed values; anything else in the URL falls back to the default. */
  readonly allowed?: readonly T[]
}

export function usePersistedState<T extends string>(
  config: PersistedStateConfig<T>,
): [T, (next: T) => void] {
  const { page, param, defaultValue, allowed } = config
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()

  const coerce = useCallback(
    (raw: string | null): T => {
      if (raw === null) return defaultValue
      if (allowed && !(allowed as readonly string[]).includes(raw)) {
        return defaultValue
      }
      return raw as T
    },
    [defaultValue, allowed],
  )

  const [value, setValue] = useState<T>(() => {
    const fromUrl = searchParams.get(param)
    if (fromUrl !== null) return coerce(fromUrl)
    // Completely bare address: fall back to the saved position without
    // depending on a URL rewrite landing (App Router does not remount the
    // page on a bare-visit resume, so reading localStorage here is the only
    // reliable path).
    if (searchParams.toString() === "") {
      const saved = loadPageParam(page, param)
      if (saved !== null) return coerce(saved)
    }
    return defaultValue
  })

  // Build on the latest URL, not just on the render-time snapshot: several
  // setters fired in one tick must compose instead of dropping each other.
  const latest = useRef(new URLSearchParams(searchParams.toString()))
  useEffect(() => {
    latest.current = new URLSearchParams(searchParams.toString())
  }, [searchParams])

  const set = useCallback(
    (next: T) => {
      setValue(next)
      savePageParam(page, param, next === defaultValue ? null : next)
      const params = new URLSearchParams(latest.current.toString())
      if (next === defaultValue || next === "") params.delete(param)
      else params.set(param, next)
      latest.current = params
      const qs = params.toString()
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [defaultValue, page, param, pathname, router],
  )

  return [value, set]
}

/**
 * Resume a page's saved position when the URL carries nothing at all —
 * i.e. it is a bare visit, not a shared link. The URL remains the source of
 * truth otherwise; this only fills in an empty address.
 */
export function useResumePageParams(
  page: string,
  params: ReadonlyArray<{ param: string; defaultValue: string }>,
): void {
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()
  const resumed = useRef(false)

  useEffect(() => {
    if (resumed.current) return
    resumed.current = true
    if (searchParams.toString() !== "") return
    const next = new URLSearchParams()
    for (const { param, defaultValue } of params) {
      const saved = loadPageParam(page, param)
      if (saved !== null && saved !== defaultValue && saved !== "") {
        next.set(param, saved)
      }
    }
    if ([...next.keys()].length > 0) {
      router.replace(`${pathname}?${next.toString()}`, { scroll: false })
    }
    // Intentionally mount-only: resuming once per fresh mount is the point.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
}
