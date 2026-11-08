"use client"

import { useCallback } from "react"
import type { DbTab, QueryResult, SchemaInfo, RedisInspect } from "../types"
import { isRedisTab, tabKey } from "../db-tabs"
import { redisPageKey, type RedisPage } from "../redis-browser"

/**
 * The tabs of the explorer: which tables are open, which one is on screen,
 * and how the tree follows them.
 *
 * A tab owns its database as well as its table, so two databases of one
 * server are two tabs and neither borrows the other's rows.
 */
export function useExplorerTabs({
  browseData,
  ensureSchema,
  ensureDbSchema,
  fetchRedisPrefix,
  redisPages,
  connSchemasRef,
  dbSchemasRef,
  setConnSchemas,
  setDbSchemas,
  setSchemaInfo,
  setError,
  setBrowseTable,
  setBrowseResult,
  setBrowseTotal,
  setSelectedId,
  setSchemaConnId,
  setRedisInspect,
  setRedisInspectKey,
  setActiveTable,
  setOpenTabs,
  openTabs,
  activeTableRef,
  rightTabRef,
  autoScrollPendingRef,
}: {
  browseData: (id: string, table: string, database?: string | null, background?: boolean) => Promise<void>
  ensureSchema: (id: string, opts?: { force?: boolean; bypass?: boolean }) => Promise<void>
  ensureDbSchema: (connId: string, database: string) => Promise<void>
  fetchRedisPrefix: (id: string, database: number, prefix: string) => Promise<void>
  redisPages: Record<string, RedisPage>
  connSchemasRef: React.MutableRefObject<Record<string, SchemaInfo>>
  dbSchemasRef: React.MutableRefObject<Record<string, SchemaInfo>>
  setConnSchemas: React.Dispatch<React.SetStateAction<Record<string, SchemaInfo>>>
  setDbSchemas: React.Dispatch<React.SetStateAction<Record<string, SchemaInfo>>>
  setSchemaInfo: React.Dispatch<React.SetStateAction<SchemaInfo | null>>
  setError: React.Dispatch<React.SetStateAction<string | null>>
  setBrowseTable: React.Dispatch<React.SetStateAction<string | null>>
  setBrowseResult: React.Dispatch<React.SetStateAction<QueryResult | null>>
  setBrowseTotal: React.Dispatch<React.SetStateAction<number | null>>
  setSelectedId: React.Dispatch<React.SetStateAction<string | null>>
  setSchemaConnId: React.Dispatch<React.SetStateAction<string | null>>
  setRedisInspect: React.Dispatch<React.SetStateAction<RedisInspect | null>>
  setRedisInspectKey: React.Dispatch<React.SetStateAction<string>>
  setActiveTable: React.Dispatch<React.SetStateAction<DbTab | null>>
  setOpenTabs: React.Dispatch<React.SetStateAction<DbTab[]>>
  /** The open tabs, so a click can find the one it names. */
  openTabs: DbTab[]
  activeTableRef: React.MutableRefObject<DbTab | null>
  rightTabRef: React.MutableRefObject<"data" | "schema" | "sql">
  autoScrollPendingRef: React.MutableRefObject<boolean>
}) {
  /**
   * Opens a table: it becomes the tab on screen, and the tree scrolls to it.
   */
  const selectTreeTable = useCallback(
    async (connId: string, table: string, database?: string | null) => {
      autoScrollPendingRef.current = false
      setError(null)
      const newActive = { connId, table, database: database ?? null }
      // Open (or reuse) a tab for this table so multiple tables can stay open.
      setOpenTabs((prev) => {
        const exists = prev.some(
          (t) => t.connId === connId && t.table === table && (t.database ?? null) === (database ?? null)
        )
        if (exists) return prev
        return [...prev, newActive]
      })
      setActiveTable(newActive)
      activeTableRef.current = newActive
      setSelectedId(connId)
      setSchemaConnId(connId)
      setBrowseTable(table)
      // schema: use in-memory, then fetch
      const schema: SchemaInfo | null = database
        ? dbSchemasRef.current[`${connId}:${database}`] ?? null
        : connSchemasRef.current[connId] ?? null
      if (schema) {
        if (database) {
          setDbSchemas((prev) =>
            prev[`${connId}:${database}`] ? prev : { ...prev, [`${connId}:${database}`]: schema }
          )
        } else {
          setConnSchemas((prev) => (prev[connId] ? prev : { ...prev, [connId]: schema }))
        }
        setSchemaInfo(schema)
      }
      if (database) {
        void ensureDbSchema(connId, database) // background revalidate / fetch
      } else {
        void ensureSchema(connId) // background revalidate / fetch
      }
      // only fetch data if currently on Data tab
      if (rightTabRef.current === "data") {
        void browseData(connId, table, database)
      }
    },
    [
      ensureDbSchema,
      ensureSchema,
      browseData,
      connSchemasRef,
      dbSchemasRef,
      setConnSchemas,
      setDbSchemas,
      setSchemaInfo,
      setError,
      setBrowseTable,
      setSelectedId,
      setSchemaConnId,
      setActiveTable,
      setOpenTabs,
      activeTableRef,
      rightTabRef,
      autoScrollPendingRef,
    ]
  )

  /** Brings a tab back on screen. */
  const activateTabEntry = useCallback(
    (t: DbTab) => {
      // Auto-scroll the tree to show the selected table.
      autoScrollPendingRef.current = !isRedisTab(t)
      if (isRedisTab(t)) {
        setSelectedId(t.connId)
        setSchemaConnId(t.connId)
        setRedisInspect(null)
        setRedisInspectKey("")
        setActiveTable(t)
        activeTableRef.current = t
        const db = t.redisDb ?? 0
        const rootKey = redisPageKey(t.connId, db, "")
        if (!redisPages[rootKey]) void fetchRedisPrefix(t.connId, db, "")
        return
      }
      // Reuse selectTreeTable to load schema/data for the tab.
      void selectTreeTable(t.connId, t.table, t.database ?? undefined)
    },
    [selectTreeTable, redisPages, fetchRedisPrefix, setSelectedId, setSchemaConnId, setRedisInspect, setRedisInspectKey, setActiveTable, activeTableRef, autoScrollPendingRef]
  )

  const activateTab = useCallback(
    (idx: number) => {
      const t = openTabs[idx]
      if (!t) return
      activateTabEntry(t)
    },
    [openTabs, activateTabEntry]
  )

  const closeTab = useCallback(
    (idx: number) => {
      setOpenTabs((prev) => {
        const next = prev.filter((_, i) => i !== idx)
        // If we closed the active tab, activate a neighbor.
        const closed = prev[idx]
        const wasActive = activeTableRef.current && closed && tabKey(activeTableRef.current) === tabKey(closed)
        if (wasActive) {
          const neighbor = next[Math.min(idx, next.length - 1)]
          if (neighbor) {
            // Defer activation to after state update.
            setTimeout(() => { activateTabEntry(neighbor) }, 0)
          } else {
            setActiveTable(null)
            activeTableRef.current = null
            setBrowseTable(null)
            setBrowseResult(null)
            setBrowseTotal(null)
          }
        }
        return next
      })
    },
    [activateTabEntry, setActiveTable, setOpenTabs, setBrowseTable, setBrowseResult, setBrowseTotal, activeTableRef]
  )

  const closeOtherTabs = useCallback(
    (idx: number) => {
      setOpenTabs((prev) => {
        const keep = prev[idx]
        if (!keep) return prev
        // Activate the kept tab if it isn't already active.
        if (!(activeTableRef.current && tabKey(activeTableRef.current) === tabKey(keep))) {
          setTimeout(() => { activateTabEntry(keep) }, 0)
        }
        return [keep]
      })
    },
    [activateTabEntry, setOpenTabs, activeTableRef]
  )

  const closeAllTabs = useCallback(() => {
    setOpenTabs([])
    setActiveTable(null)
    activeTableRef.current = null
    setBrowseTable(null)
    setBrowseResult(null)
    setBrowseTotal(null)
  }, [setActiveTable, setOpenTabs, setBrowseTable, setBrowseResult, setBrowseTotal, activeTableRef])

  return {
    selectTreeTable,
    activateTab,
    activateTabEntry,
    closeTab,
    closeOtherTabs,
    closeAllTabs,
  }
}
