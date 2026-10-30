"use client"

import { useCallback, useEffect, useRef, useState } from "react"

import {
  fetchWorkspaceExpansion,
  saveWorkspaceExpansion,
} from "./runtime-rows"

/**
 * The workspace tree's open/closed shape, shared with the desktop app through
 * the coordinator's store.
 *
 * Two sets, because both directions have to be remembered: a node the user
 * never touched keeps the default for its kind (a repository with several
 * checkouts starts open), so without the closed list a collapsed repository
 * would reopen on the next visit.
 */
export function useWorkspaceExpansion(): {
  expanded: ReadonlySet<string>
  collapsed: ReadonlySet<string>
  setOpen: (id: string, open: boolean) => void
} {
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set())
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set())
  const current = useRef<{ expanded: Set<string>; collapsed: Set<string> }>({
    expanded: new Set(),
    collapsed: new Set(),
  })

  useEffect(() => {
    let alive = true
    void fetchWorkspaceExpansion()
      .then((shape) => {
        if (!alive) return
        const next = {
          expanded: new Set(shape.expanded),
          collapsed: new Set(shape.collapsed),
        }
        current.current = next
        setExpanded(next.expanded)
        setCollapsed(next.collapsed)
      })
      .catch(() => {
        // The tree falls back to its defaults; the other client keeps its copy.
      })
    return () => {
      alive = false
    }
  }, [])

  const setOpen = useCallback((id: string, open: boolean) => {
    const next = {
      expanded: new Set(current.current.expanded),
      collapsed: new Set(current.current.collapsed),
    }
    if (open) {
      next.expanded.add(id)
      next.collapsed.delete(id)
    } else {
      next.collapsed.add(id)
      next.expanded.delete(id)
    }
    current.current = next
    setExpanded(next.expanded)
    setCollapsed(next.collapsed)
    void saveWorkspaceExpansion({
      expanded: [...next.expanded],
      collapsed: [...next.collapsed],
    }).catch(() => {
      // A failed save costs the other client the update, not this one.
    })
  }, [])

  return { expanded, collapsed, setOpen }
}
