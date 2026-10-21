"use client"

import { useCallback, useSyncExternalStore } from "react"
import type { WorkspaceGroupMode } from "./grouping"

const STORAGE_KEY = "eling.workspace-group-mode"

const MODES: readonly WorkspaceGroupMode[] = ["flat", "repo", "folder"]

/** The default arrangement: grouped by repository, with worktrees nested. */
const DEFAULT_MODE: WorkspaceGroupMode = "repo"

// One store for the whole page: the side panel and the narrow-screen
// popover are both mounted, and they must never disagree about the
// arrangement. The value is cached so the snapshot stays stable between
// renders, and mirrored to storage so it survives a reload.
let current: WorkspaceGroupMode | null = null
const listeners = new Set<() => void>()

function read(): WorkspaceGroupMode {
  if (current) return current
  if (typeof window === "undefined") return DEFAULT_MODE
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    current = MODES.includes(stored as WorkspaceGroupMode)
      ? (stored as WorkspaceGroupMode)
      : DEFAULT_MODE
  } catch {
    current = DEFAULT_MODE
  }
  return current
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/**
 * How the workspace list is arranged, remembered across visits. The choice
 * belongs to the person reading the list, not to the page, so it outlives a
 * reload.
 */
export function useWorkspaceGroupMode(): {
  mode: WorkspaceGroupMode
  setMode: (mode: WorkspaceGroupMode) => void
} {
  const mode = useSyncExternalStore(subscribe, read, () => DEFAULT_MODE)

  const setMode = useCallback((next: WorkspaceGroupMode) => {
    current = next
    try {
      window.localStorage.setItem(STORAGE_KEY, next)
    } catch {
      // A blocked storage must never break the picker.
    }
    for (const listener of listeners) listener()
  }, [])

  return { mode, setMode }
}
