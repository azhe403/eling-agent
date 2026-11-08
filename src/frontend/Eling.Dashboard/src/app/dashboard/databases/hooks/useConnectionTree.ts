"use client"

import { useCallback, useRef, useState } from "react"


/**
 * The tree of connections and tables on the left.
 *
 * The tree keeps what is expanded, and reads the schema of a connection the
 * moment it opens so the tables appear.
 */
export function useConnectionTree({
  ensureSchema,
  ensureDbSchema,
}: {
  ensureSchema: (id: string, opts?: { force?: boolean; bypass?: boolean }) => Promise<void>
  ensureDbSchema: (connId: string, database: string) => Promise<void>
}) {
  const [expandedConns, setExpandedConns] = useState<Set<string>>(new Set())
  const [expandedDbs, setExpandedDbs] = useState<Set<string>>(new Set())

  const treeItemRefs = useRef<Record<string, HTMLButtonElement | null>>({})
  const asideRef = useRef<HTMLElement | null>(null)
  const autoScrollPendingRef = useRef(false)

  /** Opens or closes a connection, and reads its schema when it opens. */
  const toggleConn = useCallback(
    (id: string) => {
      const wasExpanded = expandedConns.has(id)
      setExpandedConns((prev) => {
        const next = new Set(prev)
        if (wasExpanded) next.delete(id)
        else next.add(id)
        return next
      })
      if (!wasExpanded) void ensureSchema(id)
    },
    [expandedConns, ensureSchema]
  )

  /** Opens or closes one database of a server-level connection. */
  const toggleDb = useCallback(
    (connId: string, database: string) => {
      const key = `${connId}:${database}`
      const wasExpanded = expandedDbs.has(key)
      setExpandedDbs((prev) => {
        const next = new Set(prev)
        if (wasExpanded) next.delete(key)
        else next.add(key)
        return next
      })
      if (!wasExpanded) void ensureDbSchema(connId, database)
    },
    [expandedDbs, ensureDbSchema]
  )

  /** Lets the tree register the row of a table, so it can scroll to it. */
  const registerTreeItem = useCallback((key: string, el: HTMLButtonElement | null) => {
    treeItemRefs.current[key] = el
  }, [])

  return {
    expandedConns,
    setExpandedConns,
    expandedDbs,
    setExpandedDbs,
    treeItemRefs,
    asideRef,
    autoScrollPendingRef,
    toggleConn,
    toggleDb,
    registerTreeItem,
  }
}
