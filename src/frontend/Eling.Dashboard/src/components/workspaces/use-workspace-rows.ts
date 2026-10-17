"use client"

import { useCallback, useEffect, useState } from "react"
import { fetchRuntimes, workspaceRowsOf, type WorkspaceRow } from "./runtime-rows"

/**
 * Workspace rows for a mounted surface: one fetch on mount, then `reload`
 * whenever something outside the hook learns the list changed (the codebase
 * page calls it on the SSE "runtimes" event).
 *
 * `reload({ silent: true })` swallows errors so a background refresh keeps
 * the last good rows; the loud default surfaces them as `error`, which is
 * what the mount fetch wants. All state writes land after the first await,
 * so calling from an effect or a callback never cascades extra renders.
 */
// Module-level stale cache so a remount (e.g. navigating between dashboard
// pages that share this hook) can paint the last known rows synchronously
// instead of flashing an empty list while the fetch is in flight. Reads and
// writes happen here only; consumers stay drop-in compatible. Refresh still
// runs in the background, so rows never go stale for long.
let cachedRows: WorkspaceRow[] | null = null
let cachedError: string | null = null

export function useWorkspaceRows(): {
  rows: WorkspaceRow[]
  error: string | null
  reload: (opts?: { silent?: boolean }) => Promise<void>
} {
  const [rows, setRows] = useState<WorkspaceRow[]>(() => cachedRows ?? [])
  const [error, setError] = useState<string | null>(() => cachedError)

  const reload = useCallback(async (opts?: { silent?: boolean }) => {
    try {
      const next = workspaceRowsOf(await fetchRuntimes())
      cachedRows = next
      cachedError = null
      setRows(next)
      setError(null)
    } catch (e) {
      if (opts?.silent) return
      const msg = e instanceof Error ? e.message : "Failed to load workspaces"
      cachedError = msg
      setError(msg)
    }
  }, [])

  useEffect(() => {
    let isMounted = true
    void fetchRuntimes()
      .then((runtimes) => {
        if (!isMounted) return
        const next = workspaceRowsOf(runtimes)
        cachedRows = next
        cachedError = null
        setRows(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (!isMounted) return
        const msg =
          e instanceof Error ? e.message : "Failed to load workspaces"
        // Don't clobber a previously cached good list with an error on a
        // background refresh.
        if (cachedRows === null) {
          cachedError = msg
          setError(msg)
        }
      })
    return () => {
      isMounted = false
    }
  }, [])

  return { rows, error, reload }
}
