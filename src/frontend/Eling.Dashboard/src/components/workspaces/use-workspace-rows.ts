"use client"

import { useCallback, useEffect, useState } from "react"
import {
  fetchWorkspaceTrees,
  type WorkspaceRow,
  type WorkspaceTreesPayload,
} from "./runtime-rows"
import {
  workspaceTreesOf,
  type WorkspaceGroupMode,
  type WorkspaceNode,
  type WorkspaceTrees,
} from "./grouping"

/** One answer from the coordinator: the rows, and the list arranged three ways. */
type Snapshot = {
  readonly rows: WorkspaceRow[]
  readonly trees: WorkspaceTrees
}

const EMPTY_TREES: WorkspaceTrees = { flat: [], repo: [], folder: [] }
const EMPTY_ROWS: WorkspaceRow[] = []

// Module-level cache so a remount (e.g. navigating between dashboard pages
// that share this hook) can paint the last known list synchronously instead of
// flashing an empty one while the fetch is in flight. `inFlight` de-duplicates
// the request when several components mount in the same tick — a scope panel
// and the list beside it share one answer. Refresh still runs in the
// background, so the list never goes stale for long.
let cached: Snapshot | null = null
let cachedError: string | null = null
let inFlight: Promise<Snapshot> | null = null

function load(): Promise<Snapshot> {
  if (inFlight) return inFlight

  const promise = fetchWorkspaceTrees()
    .then((payload: WorkspaceTreesPayload) => {
      const snapshot: Snapshot = {
        rows: rowsOf(payload),
        trees: workspaceTreesOf(payload),
      }
      cached = snapshot
      cachedError = null
      return snapshot
    })
    .finally(() => {
      inFlight = null
    })

  inFlight = promise
  return promise
}

/**
 * The rows the coordinator sent, in the shape the list surfaces already use.
 * A row without a root is not a workspace and is dropped; the coordinator
 * already excluded the user-scope sentinel and opted-out workspaces.
 */
function rowsOf(payload: WorkspaceTreesPayload): WorkspaceRow[] {
  const rows = payload.rows
  if (!Array.isArray(rows)) return []
  const out: WorkspaceRow[] = []
  for (const row of rows) {
    const root = row.root
    if (!root) continue
    out.push({
      root,
      alive: row.alive === true,
      repositoryRoot: row.repositoryRoot ?? null,
    })
  }
  return out
}

function messageOf(e: unknown): string {
  return e instanceof Error ? e.message : "Failed to load workspaces"
}

/**
 * The workspace list for a mounted surface: one request on mount, then
 * `reload` whenever something outside the hook learns the list changed (the
 * codebase page calls it on the SSE "runtimes" event).
 *
 * Rows and trees come from the same answer, so a page that renders both issues
 * one request and the tree can never disagree with the rows beside it.
 * `reload({ silent: true })` swallows errors so a background refresh keeps the
 * last good list; the loud default surfaces them as `error`, which is what the
 * mount fetch wants. All state writes land after the first await, so calling
 * from an effect or a callback never cascades extra renders.
 */
export function useWorkspaceTrees(): {
  rows: WorkspaceRow[]
  trees: WorkspaceTrees
  error: string | null
  reload: (opts?: { silent?: boolean }) => Promise<void>
} {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(() => cached)
  const [error, setError] = useState<string | null>(() => cachedError)

  const reload = useCallback(async (opts?: { silent?: boolean }) => {
    try {
      const next = await load()
      setSnapshot(next)
      setError(null)
    } catch (e) {
      if (opts?.silent) return
      const msg = messageOf(e)
      cachedError = msg
      setError(msg)
    }
  }, [])

  useEffect(() => {
    let isMounted = true
    void load()
      .then((next) => {
        if (!isMounted) return
        setSnapshot(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (!isMounted) return
        // Don't clobber a previously cached good list with an error on a
        // background refresh.
        if (cached === null) {
          const msg = messageOf(e)
          cachedError = msg
          setError(msg)
        }
      })
    return () => {
      isMounted = false
    }
  }, [])

  return {
    rows: snapshot?.rows ?? EMPTY_ROWS,
    trees: snapshot?.trees ?? EMPTY_TREES,
    error,
    reload,
  }
}

/**
 * Just the rows. Kept as its own name because the pages that only need the
 * flat list (counts, the single-select fallback) should not know about the
 * arrangements; it reads the same cache, so it costs no extra request.
 */
export function useWorkspaceRows(): {
  rows: WorkspaceRow[]
  error: string | null
  reload: (opts?: { silent?: boolean }) => Promise<void>
} {
  const { rows, error, reload } = useWorkspaceTrees()
  return { rows, error, reload }
}

/** The arrangement for one mode. Switching mode re-reads the cache; no request. */
export function useWorkspaceTree(
  mode: WorkspaceGroupMode,
): readonly WorkspaceNode[] {
  const { trees } = useWorkspaceTrees()
  return trees[mode]
}
