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
export function useWorkspaceRows(): {
  rows: WorkspaceRow[]
  error: string | null
  reload: (opts?: { silent?: boolean }) => Promise<void>
} {
  const [rows, setRows] = useState<WorkspaceRow[]>([])
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async (opts?: { silent?: boolean }) => {
    try {
      setRows(workspaceRowsOf(await fetchRuntimes()))
      setError(null)
    } catch (e) {
      if (opts?.silent) return
      setError(e instanceof Error ? e.message : "Failed to load workspaces")
    }
  }, [])

  useEffect(() => {
    let isMounted = true
    void fetchRuntimes()
      .then((runtimes) => {
        if (isMounted) setRows(workspaceRowsOf(runtimes))
      })
      .catch((e: unknown) => {
        if (isMounted)
          setError(e instanceof Error ? e.message : "Failed to load workspaces")
      })
    return () => {
      isMounted = false
    }
  }, [])

  return { rows, error, reload }
}
