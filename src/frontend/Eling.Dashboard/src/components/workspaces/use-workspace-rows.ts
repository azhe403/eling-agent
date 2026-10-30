"use client"

import { useCallback, useEffect, useSyncExternalStore } from "react"
import {
  fetchWorkspaceTrees,
  type WorkspaceRow,
  type WorkspaceTreesPayload,
} from "./runtime-rows"
import {
  nodesFor,
  type WorkspaceGroupMode,
  type WorkspaceNode,
} from "./grouping"
import { useWorkspaceGroupMode } from "./use-workspace-group-mode"

const EMPTY_ROWS: WorkspaceRow[] = []

/**
 * One answer from the coordinator: the rows and the arrangement for one mode.
 */
type Answer = {
  mode: WorkspaceGroupMode
  payload: WorkspaceTreesPayload
}

type Snapshot = {
  answer: Answer | null
  error: { mode: WorkspaceGroupMode; message: string } | null
}

/**
 * One store for the whole page: the scope panel and the list beside it are
 * both mounted, and they must never disagree about the list. The answer is
 * tagged with the mode it answers, so a mode switch never shows the previous
 * mode's tree while the new one is on its way.
 *
 * A mode switch is always a fresh request — the server owns the arrangement,
 * and every tree the client renders is a current, authoritative answer — while
 * two surfaces that want the same mode at the same time share one request
 * instead of issuing two.
 */
let snapshot: Snapshot = { answer: null, error: null }
const listeners = new Set<() => void>()
const inflight = new Map<WorkspaceGroupMode, Promise<void>>()
// The mode the page is currently asking for. An answer for any other mode is
// stale the moment the reader switches, so it is dropped rather than rendered
// under the wrong arrangement.
let requestedMode: WorkspaceGroupMode | null = null
// Per-mode generations, so a slow answer can never overwrite a newer answer
// for the same mode (a reload supersedes the request it replaced).
const generation = new Map<WorkspaceGroupMode, number>()

function emit(next: Partial<Snapshot>) {
  snapshot = { ...snapshot, ...next }
  for (const listener of listeners) listener()
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

function getSnapshot(): Snapshot {
  return snapshot
}

async function load(mode: WorkspaceGroupMode, silent: boolean): Promise<void> {
  // Claiming the mode happens before the in-flight check: switching back to a
  // mode whose answer is still on its way must render that answer instead of
  // dropping it as stale.
  requestedMode = mode

  const running = inflight.get(mode)
  if (running) {
    await running
    return
  }

  const gen = (generation.get(mode) ?? 0) + 1
  generation.set(mode, gen)
  // The answer is current only while this mode is still the requested one and
  // no newer request for the same mode has started.
  const isCurrent = () =>
    requestedMode === mode && generation.get(mode) === gen

  const request = (async () => {
    try {
      const payload = await fetchWorkspaceTrees(mode)
      if (!isCurrent()) return
      emit({ answer: { mode, payload }, error: null })
    } catch (e) {
      if (!isCurrent()) return
      // A silent refresh keeps the last good list; a loud one says what went
      // wrong. Either way the answer stays: an empty list would be a lie.
      if (silent) return
      emit({
        error: {
          mode,
          message:
            e instanceof Error ? e.message : "Failed to load workspaces",
        },
      })
    }
  })()

  inflight.set(mode, request)
  try {
    await request
  } finally {
    if (inflight.get(mode) === request) inflight.delete(mode)
  }
}

/**
 * The workspace list for a mounted surface, one arrangement at a time.
 *
 * `mode` selects which request to issue; switching modes re-issues the fetch
 * for the new mode.
 */
export function useWorkspaceTrees(
  mode: WorkspaceGroupMode,
): {
  rows: WorkspaceRow[]
  nodes: readonly WorkspaceNode[]
  error: string | null
  reload: (opts?: { silent?: boolean }) => Promise<void>
} {
  const state = useSyncExternalStore(subscribe, getSnapshot, getSnapshot)

  useEffect(() => {
    void load(mode, false)
  }, [mode])

  const reload = useCallback(
    async (opts?: { silent?: boolean }) => {
      inflight.delete(mode)
      await load(mode, opts?.silent ?? false)
    },
    [mode],
  )

  const answer = state.answer?.mode === mode ? state.answer.payload : null

  return {
    rows: rowsOf(answer),
    nodes: nodesFor(answer ?? {}),
    error: state.error?.mode === mode ? state.error.message : null,
    reload,
  }
}

/**
 * Just the rows of the list, read through the same request the tree uses, so
 * the two can never show different workspaces. The mode is the one the reader
 * picked, so both surfaces ask the coordinator for the same arrangement.
 */
export function useWorkspaceRows(): {
  rows: WorkspaceRow[]
  error: string | null
  reload: (opts?: { silent?: boolean }) => Promise<void>
} {
  const { mode } = useWorkspaceGroupMode()
  const { rows, error, reload } = useWorkspaceTrees(mode)
  return { rows, error, reload }
}

/**
 * Just the rows, in the shape the list surfaces already use. A row without a
 * root is not a workspace and is dropped; the coordinator already excluded the
 * user-scope sentinel and opted-out workspaces.
 */
function rowsOf(payload: WorkspaceTreesPayload | null): WorkspaceRow[] {
  if (!payload) return EMPTY_ROWS
  const rows = payload.rows
  if (!Array.isArray(rows)) return EMPTY_ROWS
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
