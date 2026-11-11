"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { RefreshCw, Plus } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"

import { DbTab, isRedisTab, tabKey } from "./db-tabs"
import { RedisDbInfo, fetchRedisDatabases, pickRedisDb, parseRedisDb } from "./redis-browser"
import {
  DEFAULT_CONN_ORDER,
  DEFAULT_CONN_SORT,
  connSortQuery,
  resolveConnSort,
  type ConnSortKey,
  type ConnSortOrder,
} from "./conn-sorting"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import { useMediaQuery } from "@/hooks/use-media-query"
import { XL_BREAKPOINT } from "@/lib/constants"

import type { DbConn, Detected, QueryResult } from "./types"
import { API } from "./utils"
import { apiFetch } from "@/lib/api"

import { useNotifications } from "./hooks/useNotifications"
import { useConnectionForm } from "./hooks/useConnectionForm"
import { useSchema } from "./hooks/useSchema"
import { useRedisBrowser } from "./hooks/useRedisBrowser"
import { useTableData, useTableMutations } from "./hooks/useTableData"
import { useExplorerTabs } from "./hooks/useExplorerTabs"

import { DetectedConnectionsCard } from "./components/DetectedConnectionsCard"
import { ExplorerPanels } from "./components/ExplorerPanels"
import { ConnectionForm } from "./components/connection-form/ConnectionForm"
import { DeleteConnectionDialog } from "./components/dialogs/DeleteConnectionDialog"
import { ImportConfirmDialog } from "./components/dialogs/ImportConfirmDialog"
import { DeleteRowConfirmDialog } from "./components/dialogs/DeleteRowConfirmDialog"
import { ReadOnlyDeleteDialog } from "./components/dialogs/ReadOnlyDeleteDialog"
import { HostPathPicker } from "@/components/host-path-picker"
import { NotificationStack } from "./components/notifications/NotificationToast"
import { TreeContextMenu, type TreeContextMenuState } from "./components/context-menus/TreeContextMenu"
import { TabContextMenu, type TabContextMenuState } from "./components/context-menus/TabContextMenu"
import { CellContextMenu, type CellContextMenuState } from "./components/context-menus/CellContextMenu"

/** The pair the first fetch should ask for: the URL, then the remembered pair, then the default. */
function initialConnSort(): { sort: ConnSortKey; order: ConnSortOrder } {
  if (typeof window === "undefined") {
    return { sort: DEFAULT_CONN_SORT, order: DEFAULT_CONN_ORDER }
  }
  try {
    const params = new URLSearchParams(window.location.search)
    const raw = localStorage.getItem("db.explorer.state")
    const st = raw ? JSON.parse(raw) : null
    return resolveConnSort({
      urlSort: params.get("sort"),
      urlOrder: params.get("order"),
      savedSort: st?.sort,
      savedOrder: st?.order,
    })
  } catch {
    return { sort: DEFAULT_CONN_SORT, order: DEFAULT_CONN_ORDER }
  }
}

export function Databases() {
  const { notifications, notify: notifyDb, dismiss: dismissNotification } = useNotifications()

  // Connections
  const [conns, setConns] = useState<DbConn[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [detected, setDetected] = useState<Detected[]>([])
  const [selectedKeys, setSelectedKeys] = useState<string[]>([])
  const [deletingConn, setDeletingConn] = useState<DbConn | null>(null)

  // The order the connections list is drawn in. The pair lives in the URL
  // (a shared link or the back button is a deliberate choice) and in
  // localStorage (the choice the reader made last time). It is resolved in a
  // lazy initializer rather than the restore effect: the restore effect runs
  // after the first load(), so resolving there would fetch the default order
  // and then visibly reorder the list on every reload.
  const [connSort, setConnSort] = useState<ConnSortKey>(() => initialConnSort().sort)
  const [connOrder, setConnOrder] = useState<ConnSortOrder>(() => initialConnSort().order)

  // The list fades out, the new order is painted into it, and it fades back
  // in. Everything else (an error, a refresh, the initial load) goes through
  // the same path, so "sort" and "reload" never look different. Two phases are
  // needed because the DOM cannot both show the old rows fading out and the
  // new rows at the same instant.
  const [isListFading, setIsListFading] = useState(false)
  const fadeOutMs = 150

  // The tree
  const [expandedConns, setExpandedConns] = useState<Set<string>>(new Set())
  const [expandedDbs, setExpandedDbs] = useState<Set<string>>(new Set())
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [schemaConnId, setSchemaConnId] = useState<string | null>(null)
  const [treeCtxMenu, setTreeCtxMenu] = useState<TreeContextMenuState | null>(null)
  const treeItemRefs = useRef<Record<string, HTMLButtonElement | null>>({})
  const asideRef = useRef<HTMLElement | null>(null)
  const autoScrollPendingRef = useRef(false)

  // The tab that is on screen. The ref mirrors it, so a request that is
  // already on its way can tell which table it was asked for.
  const [activeTable, setActiveTable] = useState<DbTab | null>(null)
  const [openTabs, setOpenTabs] = useState<DbTab[]>([])
  const [rightTab, setRightTab] = useState<"data" | "schema" | "sql">("data")
  const [tabCtxMenu, setTabCtxMenu] = useState<TabContextMenuState | null>(null)
  const rightTabRef = useRef(rightTab)
  const activeTableRef = useRef<DbTab | null>(null)
  const activeTabElRef = useRef<HTMLDivElement | null>(null)
  useEffect(() => { rightTabRef.current = rightTab }, [rightTab])
  useEffect(() => { activeTableRef.current = activeTable }, [activeTable])

  // The grid
  const [cellCtxMenu, setCellCtxMenu] = useState<CellContextMenuState | null>(null)

  // Schemas
  //
  // A Redis connection is not a table store: /schema refuses it, so the schema
  // hook is told which connections it may ask about. A connection that is not
  // in the list yet is still allowed through - `conns` loads asynchronously,
  // and refusing an unknown id would skip the one read that matters when the
  // list is empty on first paint.
  const connsRef = useRef<DbConn[]>([])
  useEffect(() => { connsRef.current = conns }, [conns])
  const isSqlConnection = useCallback((connId: string) => {
    const conn = connsRef.current.find((c) => c.id === connId)
    return conn ? conn.provider !== "Redis" : true
  }, [])

  // A 404 from /schema can only mean the connection is gone from the registry
  // (deleted in another tab or by the agent, or this page is pointed at another
  // backend). The list re-reads itself, throttled: one dead row in a tree full
  // of them must not fan out into a reload per row.
  const reloadListRef = useRef<() => void>(() => {})
  const lastMissingReloadAt = useRef(0)
  const handleConnectionMissing = useCallback(() => {
    const now = Date.now()
    if (now - lastMissingReloadAt.current < 2000) return
    lastMissingReloadAt.current = now
    reloadListRef.current()
  }, [])

  const {
    schemaInfo,
    setSchemaInfo,
    connSchemas,
    setConnSchemas,
    dbSchemas,
    setDbSchemas,
    schemaLoading,
    connSchemasRef,
    dbSchemasRef,
    schemaConnIdRef,
    ensureSchema,
    ensureDbSchema,
    loadSchema,
    retryLabel,
    KEEP_FRESH_MS,
  } = useSchema(notifyDb, isSqlConnection, handleConnectionMissing)

  // Redis
  const {
    redisDbs,
    setRedisDbs,
    redisPages,
    redisExpanded,
    redisInspectKey,
    setRedisInspectKey,
    redisLoading,
    redisInspect,
    setRedisInspect,
    redisInspectOpen,
    setRedisInspectOpen,
    redisPrefix,
    setRedisPrefix,
    redisRootPrefix,
    redisDeleteTarget,
    setRedisDeleteTarget,
    redisDeleteOpen,
    setRedisDeleteOpen,
    redisPrefixPreviewTarget,
    redisPrefixPreviewOpen,
    setRedisPrefixPreviewOpen,
    redisPreview,
    redisPreviewLoading,
    redisMutating,
    redisAutoRefresh,
    setRedisAutoRefresh,
    redisAutoRefreshMs,
    setRedisAutoRefreshMs,
    loadRedisDatabases,
    fetchRedisPrefix,
    loadMoreRedis,
    browseRedisPrefix,
    rootRedisPrefix,
    collapseRedis,
    toggleRedisPrefix,
    selectRedisKey,
    toggleRedisConn,
    openRedisDb,
    deleteRedisKey,
    previewRedisPrefix,
    deleteRedisPrefix,
  } = useRedisBrowser({
    notifyDb,
    activeTable,
    setActiveTable,
    activeTableRef,
    setSelectedId,
    setSchemaConnId,
    setOpenTabs,
    setRightTab,
    expandedConns,
    setExpandedConns,
    conns,
  })

  const activeRedisDb = isRedisTab(activeTable ?? null) ? (activeTable?.redisDb ?? 0) : 0

  // SQL
  const [sql, setSql] = useState("SELECT 1")
  const [qResult, setQResult] = useState<QueryResult | null>(null)
  const [qLoading, setQLoading] = useState(false)

  // Forms
  const {
    create,
    setCreate,
    createEffectiveName,
    createPortInvalid,
    createReady,
    createPayload,
    resetCreate,
    changeCreateProvider,
    edit,
    setEdit,
    editPortInvalid,
    editReady,
    editPayload,
    openEdit,
    closeEdit,
    createServerKey,
    editServerKey,
  } = useConnectionForm()
  const [showCreate, setShowCreate] = useState(false)
  const [createTestResult, setCreateTestResult] = useState<{ ok: boolean; message: string; latencyMs: number } | null>(null)
  const [createTesting, setCreateTesting] = useState(false)
  const [editTestResult, setEditTestResult] = useState<{ ok: boolean; message: string; latencyMs: number } | null>(null)
  const [editTesting, setEditTesting] = useState(false)

  // The path picker dialog (SQLite file selection)
  const [showPicker, setShowPicker] = useState(false)
  const [pickerTarget, setPickerTarget] = useState<"create" | "edit">("create")

  // Import
  const [showImportConfirm, setShowImportConfirm] = useState(false)

  const selectedConn = conns.find((c) => c.id === selectedId)
  const selectedIsReadOnly = selectedConn?.environment === "ReadOnly"
  const selectedIsRedis = selectedConn?.provider === "Redis"

  const isWide = useMediaQuery(`(min-width: ${XL_BREAKPOINT}px)`)

  /* ---------------------------------------------------------------- the list */

  const load = useCallback(async () => {
    setLoading(true)
    const reducedMotion = typeof window !== "undefined" && window.matchMedia("(prefers-reduced-motion: reduce)").matches
    // Fade the visible connections out before the request starts, so the list
    // never re-flows under a reader mid-glance.
    if (!reducedMotion) setIsListFading(true)
    try {
      if (!reducedMotion) await new Promise((resolve) => setTimeout(resolve, fadeOutMs))

      const data = await apiFetch<DbConn[]>(API + connSortQuery(connSort, connOrder), { cache: "no-store" })
      setConns(data)
      // The list is ground truth: an expanded id that is not in it anymore
      // (connection deleted elsewhere) is pruned here, or the 30-second
      // freshness loop keeps firing requests at a corpse forever.
      const valid = new Set(data.map((c) => c.id))
      setExpandedConns((prev) => {
        const next = new Set([...prev].filter((id) => valid.has(id)))
        return next.size === prev.size ? prev : next
      })
      setExpandedDbs((prev) => {
        const next = new Set([...prev].filter((key) => valid.has(key.slice(0, key.indexOf(":")))))
        return next.size === prev.size ? prev : next
      })
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "failed")
    } finally {
      setLoading(false)
      setIsListFading(false)
    }
  }, [notifyDb, connSort, connOrder])

  useEffect(() => {
    const id = setTimeout(() => { void load() }, 0)
    return () => clearTimeout(id)
  }, [load])

  // A connection added outside this tab - the agent's db_import_connections,
  // a second browser tab, or another eling process - writes the shared registry
  // file, and the backend broadcasts "databases" for it. Reload so the new
  // connection appears without anybody pressing Refresh.
  const reloadOnRegistryChange = useCallback(() => { void load() }, [load])
  useMemoriesSse(reloadOnRegistryChange, undefined, ["databases"])
  // The schema hook 404s through the same door: gone from the registry means
  // the list is stale, so it asks for a fresh one.
  useEffect(() => { reloadListRef.current = reloadOnRegistryChange }, [reloadOnRegistryChange])

  /* ------------------------------------------------------------ the schemas */

  // Keep what is on screen current: a tree left open a while should not go
  // stale. The read is cache-first, so it paints from the snapshot and asks
  // again - it never blocks the screen. The next tick is half a minute away,
  // so a slow database cannot stack two of them.
  useEffect(() => {
    const timer = setInterval(() => {
      for (const id of expandedConns) void ensureSchema(id, { force: true })
      for (const key of expandedDbs) {
        const cut = key.indexOf(":")
        if (cut <= 0) continue
        void ensureDbSchema(key.slice(0, cut), key.slice(cut + 1))
      }
    }, KEEP_FRESH_MS)
    return () => clearInterval(timer)
  }, [expandedConns, expandedDbs, ensureSchema, ensureDbSchema, KEEP_FRESH_MS])

  // Auto-load schema for any expanded connection that doesn't have one yet
  // (covers restore / stale state where "Loading." would otherwise stick).
  useEffect(() => {
    for (const id of expandedConns) {
      if (!connSchemas[id]) void ensureSchema(id)
    }
  }, [expandedConns, connSchemas, ensureSchema])

  useEffect(() => { schemaConnIdRef.current = schemaConnId }, [schemaConnId])

  useEffect(() => {
    for (const id of expandedConns) {
      const conn = conns.find((c) => c.id === id)
      if (conn?.provider === "Redis" && !redisDbs[id]) void loadRedisDatabases(id)
    }
  }, [expandedConns, conns, redisDbs, loadRedisDatabases])

  /* ---------------------------------------------------------------- the grid */

  const {
    setBrowseTable,
    browseResult,
    setBrowseResult,
    browseTotal,
    setBrowseTotal,
    browseLoading,
    browseData,
  } = useTableData({
    notifyDb,
    conns,
    activeTable,
    activeTableRef,
    selectedId,
    selectedConn,
    selectedIsReadOnly,
    schemaInfo,
  })

  const refreshData = useCallback(async () => {
    if (!activeTable) return
    // The active tab owns its database; the selection does not. Polling from
    // the selection wrote another database's rows under this table's name.
    await browseData(activeTable.connId, activeTable.table, activeTable.database, false, true)
  }, [activeTable, browseData])

  const {
    pkNames,
    activeTableSchema,
    mutating,
    savingCell,
    editingCell,
    editValue,
    setEditValue,
    newRow,
    setNewRow,
    deleteRowTarget,
    setDeleteRowTarget,
    showReadOnlyDeleteDialog,
    setShowReadOnlyDeleteDialog,
    startEdit,
    cancelEdit,
    commitEdit,
    setCellValue,
    promptDeleteRow,
    confirmDeleteRow,
    submitNewRow,
  } = useTableMutations({
    notifyDb,
    browseResult,
    setBrowseResult,
    schemaInfo,
    activeTable,
    selectedId,
    selectedConn,
    selectedIsReadOnly,
    refreshData,
  })

  /* ------------------------------------------------------------------- tabs */

  const {
    selectTreeTable,
    activateTab,
    closeTab,
    closeOtherTabs,
    closeAllTabs,
  } = useExplorerTabs({
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
  })

  /* ------------------------------------------------------------------- tree */

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

  const registerTreeItem = useCallback((key: string, el: HTMLButtonElement | null) => {
    treeItemRefs.current[key] = el
  }, [])

  // Auto-scroll the active table into view in the tree. Re-run when the tree
  // re-renders (schema loaded / connection expanded) so a previously selected
  // table is scrolled into view after restore, not only on fresh selection.
  useEffect(() => {
    if (!activeTable) return
    // Only auto-scroll on restore, not on manual selection
    if (!autoScrollPendingRef.current) return
    const key = activeTable.database
      ? `${activeTable.connId}:${activeTable.database}:${activeTable.table}`
      : `${activeTable.connId}:${activeTable.table}`
    let cancelled = false
    const scroll = () => {
      if (cancelled) return
      const el = treeItemRefs.current[key]
      const aside = asideRef.current
      if (el && aside) {
        // manual scroll is more reliable than scrollIntoView for nested
        // scroll containers
        requestAnimationFrame(() => {
          if (cancelled) return
          const el2 = treeItemRefs.current[key]
          const aside2 = asideRef.current
          if (el2 && aside2) {
            const asideRect = aside2.getBoundingClientRect()
            const elRect = el2.getBoundingClientRect()
            // offset of element relative to aside's scroll content
            const offset = elRect.top - asideRect.top + aside2.scrollTop
            const target = offset - aside2.clientHeight / 2 + el2.clientHeight / 2
            aside2.scrollTo({ top: Math.max(0, target), behavior: "smooth" })
            autoScrollPendingRef.current = false
          }
        })
      } else if (el) {
        el.scrollIntoView({ block: "nearest", behavior: "smooth" })
      }
    }
    scroll()
    const t1 = setTimeout(scroll, 50)
    const t2 = setTimeout(scroll, 250)
    const t3 = setTimeout(scroll, 600)
    return () => {
      cancelled = true
      clearTimeout(t1)
      clearTimeout(t2)
      clearTimeout(t3)
    }
  }, [activeTable, connSchemas, expandedConns])

  // Auto-scroll the active table tab into view in the (possibly overflowing) tab strip.
  useEffect(() => {
    const el = activeTabElRef.current
    if (el) {
      el.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "smooth" })
    }
  }, [activeTable])

  /* --------------------------------------------------------- restore & keep */

  const restoredRef = useRef(false)
  // Restore persisted explorer position after connections load
  useEffect(() => {
    if (loading || restoredRef.current) return
    restoredRef.current = true
    try {
      const params = new URLSearchParams(window.location.search)
      const urlConn = params.get("connectionId")
      const urlTable = params.get("table")
      const urlTab = params.get("tab") as "data" | "schema" | "sql" | null

      const raw = localStorage.getItem("db.explorer.state")
      const st = raw ? JSON.parse(raw) : null

      const connId = urlConn || st?.schemaConnId || st?.selectedId
      const tab = urlTab || st?.rightTab || "data"

      const savedTab = (st?.activeTable as DbTab | null) ?? null

      // eslint-disable-next-line react-hooks/set-state-in-effect -- restoring the saved explorer position is a one-shot hydrate from localStorage and the URL.
      if (st?.expandedConns) setExpandedConns(new Set(st.expandedConns))
      if (Array.isArray(st?.openTabs)) setOpenTabs(st.openTabs)
      if (connId) {
        setSelectedId(connId)
        setSchemaConnId(connId)
        setExpandedConns((prev) => {
          const next = new Set(prev)
          next.add(connId)
          return next
        })
      }
      setRightTab(tab)

      const table = urlTable || savedTab?.table
      const database = params.get("database") || savedTab?.database || null
      const savedRedisForThisConn = savedTab?.kind === "redis" && savedTab.connId === connId
      const restoredKind = isRedisTab({ table }) || savedRedisForThisConn ? ("redis" as const) : undefined
      if (connId && table && restoredKind === "redis") {
        void (async () => {
          // The URL names the database, or the saved tab does. Only when
          // neither does is the list needed - and then it has to be read
          // before the tab opens, or the fallback "first database that holds
          // keys" can only ever be db0.
          const urlDb = params.get("redisDb")
          const savedDb = savedTab && savedTab.connId === connId ? savedTab : null
          let available = redisDbs[connId] ?? []
          if (parseRedisDb(urlDb) === null && typeof savedDb?.redisDb !== "number") {
            available = await fetchRedisDatabases(connId).catch(() => [] as RedisDbInfo[])
            setRedisDbs((prev) => ({ ...prev, [connId]: available }))
          }

          const db = pickRedisDb({ urlDb, savedTab: savedDb, connId, available })
          const restored: DbTab = { connId, table, database: null, kind: "redis", redisDb: db }
          setActiveTable(restored)
          activeTableRef.current = restored
          setOpenTabs((prev) => (prev.some((t) => tabKey(t) === tabKey(restored)) ? prev : [...prev, restored]))
          // Reloading used to restore the db selection but leave the card empty,
          // so a refresh looked like the browser had lost the keys.
          void fetchRedisPrefix(connId, db, "")
        })()
      } else if (connId && table) {
        autoScrollPendingRef.current = true
        const restored = { connId, table, database: database ?? null }
        setActiveTable(restored)
        activeTableRef.current = restored
        setOpenTabs((prev) =>
          prev.some(
            (t) => t.connId === connId && t.table === table && (t.database ?? null) === (database ?? null)
          )
            ? prev
            : [...prev, restored]
        )
        setBrowseTable(table)
        if (database) {
          setExpandedDbs((prev) => {
            const next = new Set(prev)
            next.add(`${connId}:${database}`)
            return next
          })
        }
        void (async () => {
          // backend (possibly cached) is the source of truth now
          if (database) {
            void ensureDbSchema(connId, database)
          } else {
            void ensureSchema(connId)
          }
          if (tab === "data") {
            void browseData(connId, table, database)
          }
        })()
      }
    } catch {
      // ignore
    }
  }, [loading])

  // Persist explorer position (only after restore, to avoid clobbering saved state on mount)
  useEffect(() => {
    if (!restoredRef.current) return
    try {
      localStorage.setItem(
        "db.explorer.state",
        JSON.stringify({
          expandedConns: Array.from(expandedConns),
          rightTab,
          selectedId,
          schemaConnId,
          activeTable,
          openTabs,
          sort: connSort,
          order: connOrder,
        })
      )
      const params = new URLSearchParams()
      if (schemaConnId) params.set("connectionId", schemaConnId)
      if (activeTable?.table) params.set("table", activeTable.table)
      if (activeTable?.database) params.set("database", activeTable.database)
      if (rightTab) params.set("tab", rightTab)
      // The chosen pair travels in the URL only when it differs from the
      // default, so a shared link stays as short as it was before sorting
      // existed.
      if (connSort !== DEFAULT_CONN_SORT || connOrder !== DEFAULT_CONN_ORDER) {
        params.set("sort", connSort)
        params.set("order", connOrder)
      }
      // The Redis panel's db selection is part of the view, so it belongs in
      // the URL: without it a refresh dropped back to db0 while the tree above
      // still showed the keys of the db that was actually selected.
      if (isRedisTab(activeTable)) params.set("redisDb", String(activeTable?.redisDb ?? 0))
      const qs = params.toString()
      window.history.replaceState(null, "", qs ? "?" + qs : window.location.pathname)
    } catch {
      // ignore
    }
  }, [expandedConns, rightTab, selectedId, schemaConnId, activeTable, openTabs, connSort, connOrder])

  /* ------------------------------------------------------------- connection */

  const removeConnection = useCallback(
    async (id: string) => {
      await apiFetch<unknown>(API + "/" + id, { method: "DELETE" })
      await load()
    },
    [load]
  )

  const testConnectionCreate = useCallback(async () => {
    if (createPortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    setCreateTesting(true)
    setCreateTestResult(null)
    try {
      const result = await apiFetch<{ ok: boolean; message: string; latencyMs: number }>(API + "/test-connection", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(createPayload()),
      })
      setCreateTestResult(result)
    } catch (e: unknown) {
      setCreateTestResult({ ok: false, message: e instanceof Error ? e.message : "failed", latencyMs: 0 })
    } finally {
      setCreateTesting(false)
    }
  }, [createPortInvalid, createPayload, notifyDb])

  const testConnectionEdit = useCallback(async () => {
    const body = editPayload()
    if (!edit || !body) return
    if (editPortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    setEditTesting(true)
    setEditTestResult(null)
    try {
      let result: { ok: boolean; message: string; latencyMs: number }
      if (edit.mode === "raw") {
        if (edit.connStr.trim() !== "") {
          result = await apiFetch<{ ok: boolean; message: string; latencyMs: number }>(API + "/test-connection", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              name: edit.name,
              provider: edit.provider,
              connectionString: edit.connStr,
              environment: edit.environment,
              allowAgentMutations: edit.allowAgentMutations,
              requireApprovalForMutations: edit.requireApprovalForMutations,
            }),
          })
        } else {
          result = await apiFetch<{ ok: boolean; message: string; latencyMs: number }>(API + "/" + edit.id + "/test", { method: "POST" })
        }
      } else {
        // The edited fields are layered over the stored connection on the
        // server, so a blank password keeps the stored one.
        result = await apiFetch<{ ok: boolean; message: string; latencyMs: number }>(API + "/" + edit.id + "/test", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ manual: body.manual }),
        })
      }
      setEditTestResult(result)
    } catch (e: unknown) {
      setEditTestResult({ ok: false, message: e instanceof Error ? e.message : "failed", latencyMs: 0 })
    } finally {
      setEditTesting(false)
    }
  }, [edit, editPayload, editPortInvalid, notifyDb])

  const submitCreate = useCallback(async () => {
    if (createPortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    try {
      await apiFetch<unknown>(API, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(createPayload()),
      })
      setShowCreate(false)
      resetCreate()
      await load()
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "create failed")
    }
  }, [createPortInvalid, notifyDb, createPayload, resetCreate, load])

  const submitEdit = useCallback(async () => {
    const body = editPayload()
    if (!body) return
    if (editPortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    try {
      await apiFetch<unknown>(API + "/" + edit?.id, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      })
      closeEdit()
      await load()
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "update failed")
    }
  }, [editPayload, editPortInvalid, edit, notifyDb, closeEdit, load])

  /**
   * The databases on the server the create form points at. The credentials
   * are not saved yet, so this asks the backend to read them off the server
   * with what was just typed.
   */
  const loadCreateDatabases = useCallback(async (): Promise<string[]> => {
    const data = await apiFetch<unknown>(API + "/list-databases", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(createPayload()),
    })
    return Array.isArray(data) ? data : []
  }, [createPayload])

  /**
   * The databases on the server the edited connection points at. The stored
   * password is not in the form, so the backend layers the entered fields over
   * the stored connection.
   */
  const loadEditDatabases = useCallback(async (): Promise<string[]> => {
    if (!edit) return []
    const data = await apiFetch<unknown>(API + "/" + edit.id + "/databases", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ manual: editPayload()?.manual }),
    })
    return Array.isArray(data) ? data : []
  }, [edit, editPayload])

  /* ---------------------------------------------------------------- the SQL */

  const runQuery = useCallback(async () => {
    if (!selectedId) return
    setQLoading(true)
    setQResult(null)
    try {
      const trimmed = sql.trim()
      if (/^\s*(insert|update|delete|create|alter|drop|truncate|replace)\b/i.test(trimmed)) {
        if (selectedIsReadOnly) {
          notifyDb("error", "Read-only connection", "This connection is read-only, so data changes are disabled.")
          return
        }
        const data = await apiFetch<{ affectedRows: number; truncated: boolean }>(API + "/" + selectedId + "/mutate", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sql: trimmed, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
        })
        setQResult({
          columns: ["affectedRows", "truncated"],
          rows: [[data.affectedRows, data.truncated]],
          truncated: false,
        })
        return
      }
      let finalSql = trimmed
      if (!/\blimit\b/i.test(finalSql)) {
        finalSql = finalSql.replace(/;?\s*$/, "") + " LIMIT 100"
      }
      const qData = await apiFetch<QueryResult>(API + "/" + selectedId + "/query", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: finalSql, maxRows: 100, database: activeTable?.database ?? null }),
      })
      setQResult(qData)
      if (qData.fromCache) {
        const conn = conns.find((c) => c.id === selectedId)
        notifyDb(
          "warning",
          "Cached result",
          `${conn?.name ?? selectedId}: database unavailable, showing snapshot from ${qData.cachedAt ?? "earlier"}`
        )
      }
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : "query failed"
      const conn = conns.find((c) => c.id === selectedId)
      notifyDb("error", "Query failed", `${conn?.name ?? selectedId}: ${msg}`)
    } finally {
      setQLoading(false)
    }
  }, [selectedId, sql, selectedIsReadOnly, activeTable, notifyDb, conns])

  /* ---------------------------------------------------------------- import */

  const doImport = useCallback(async () => {
    await apiFetch<unknown>(API + "/import", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ settingsKeys: selectedKeys }),
    })
    setSelectedKeys([])
    setDetected([])
    setShowImportConfirm(false)
    await load()
  }, [selectedKeys, load])

  const openImportConfirm = useCallback(() => {
    if (selectedKeys.length === 0) return
    setShowImportConfirm(true)
  }, [selectedKeys])

  /* ----------------------------------------------------------------- picker */

  const openPicker = useCallback((target: "create" | "edit") => {
    setPickerTarget(target)
    setShowPicker(true)
  }, [])

  /* ----------------------------------------------------------------- render */

  return (
    <div className="flex h-screen flex-col overflow-hidden">
      <div className="shrink-0">
        <DashboardHeader label="Databases">
          <Button
            size="sm"
            onClick={() => { if (showCreate) { setShowCreate(false) } else { resetCreate(); setShowCreate(true) } }}
          >
            <Plus className="mr-2 size-4" /> New connection
          </Button>

          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              void load()
              for (const id of expandedConns) {
                void ensureSchema(id, { force: true, bypass: true })
                const conn = conns.find((c) => c.id === id)
                if (conn?.provider === "Redis") void loadRedisDatabases(id)
              }
            }}
          >
            <RefreshCw className="mr-2 size-4" />
            <span className="hidden sm:inline-block">Refresh</span>
          </Button>
        </DashboardHeader>
      </div>

      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-hidden p-4 pt-2">
        {error ? <p className="text-sm text-destructive">{error}</p> : null}

        <DetectedConnectionsCard
          detected={detected}
          selectedKeys={selectedKeys}
          onToggleKey={(settingsKey, selected) => {
            if (selected) setSelectedKeys([...selectedKeys, settingsKey])
            else setSelectedKeys(selectedKeys.filter((k) => k !== settingsKey))
          }}
          onImport={openImportConfirm}
        />

        <ExplorerPanels
          conns={conns}
          expandedConns={expandedConns}
          expandedDbs={expandedDbs}
          connSchemas={connSchemas}
          dbSchemas={dbSchemas}
          redisDbs={redisDbs}
          activeTable={activeTable}
          openTabs={openTabs}
          onActivateTab={activateTab}
          onCloseTab={closeTab}
          onTabContextMenu={(e, idx) => {
            e.preventDefault()
            setTabCtxMenu({ x: e.clientX, y: e.clientY, idx })
          }}
          rightTab={rightTab}
          connSort={connSort}
          connOrder={connOrder}
          loading={loading}
          isFading={isListFading}
          schemaConnId={schemaConnId}
          schemaInfo={schemaInfo}
          schemaLoading={schemaLoading}
          selectedId={selectedId}
          selectedIsReadOnly={selectedIsReadOnly}
          selectedIsRedis={selectedIsRedis}
          browseResult={browseResult}
          browseTotal={browseTotal}
          browseLoading={browseLoading}
          mutating={mutating}
          editingCell={editingCell}
          editValue={editValue}
          savingCell={savingCell}
          newRow={newRow}
          pkNames={pkNames}
          sql={sql}
          qResult={qResult}
          qLoading={qLoading}
          activeRedisDb={activeRedisDb}
          redisPrefix={redisPrefix}
          redisRootPrefix={redisRootPrefix}
          redisPages={redisPages}
          redisExpanded={redisExpanded}
          redisInspectKey={redisInspectKey}
          redisLoading={redisLoading}
          redisInspect={redisInspect}
          redisInspectOpen={redisInspectOpen}
          redisDeleteTarget={redisDeleteTarget}
          redisDeleteOpen={redisDeleteOpen}
          redisPreview={redisPreview}
          redisPreviewLoading={redisPreviewLoading}
          redisMutating={redisMutating}
          redisPrefixPreviewOpen={redisPrefixPreviewOpen}
          redisPrefixPreviewTarget={redisPrefixPreviewTarget}
          isWide={isWide}
          retryLabel={retryLabel}
          onSortChange={setConnSort}
          onOrderChange={setConnOrder}
          onToggleConn={toggleConn}
          onToggleRedisConn={toggleRedisConn}
          onToggleDb={toggleDb}
          onSelectTreeTable={selectTreeTable}
          onOpenRedisDb={openRedisDb}
          onTreeContextMenu={(e, connId, table, database) => {
            e.preventDefault()
            setTreeCtxMenu({ x: e.clientX, y: e.clientY, connId, table, database })
          }}
          onRegisterTreeItem={registerTreeItem}
          onRightTabChange={setRightTab}
          onSchemaClose={() => setSchemaConnId(null)}
          onSchemaTableSelect={(table) => {
            setActiveTable({ connId: schemaConnId!, table })
            setBrowseTable(table)
            setRightTab("schema")
          }}
          onRefreshData={() => { void refreshData() }}
          onAddRow={() => {
            if (browseResult) setNewRow(Object.fromEntries(browseResult.columns.map((c) => [c, ""])))
          }}
          onStartEdit={startEdit}
          onEditValueChange={setEditValue}
          onCommitEdit={commitEdit}
          onCancelEdit={cancelEdit}
          onNewRowChange={setNewRow}
          onSubmitNewRow={submitNewRow}
          onPromptDeleteRow={promptDeleteRow}
          onCellContextMenu={(e, row, col) => {
            // keep the menu inside the viewport
            const menuW = 200
            const menuH = 200
            const x = Math.min(e.clientX, window.innerWidth - menuW - 8)
            const y = Math.min(e.clientY, window.innerHeight - menuH - 8)
            setCellCtxMenu({ x: Math.max(8, x), y: Math.max(8, y), row, col })
          }}
          onSqlChange={setSql}
          onRunQuery={() => { void runQuery() }}
          onPrefixChange={setRedisPrefix}
          onBrowsePrefix={browseRedisPrefix}
          onRootPrefix={rootRedisPrefix}
          onTogglePrefix={toggleRedisPrefix}
          onSelectKey={selectRedisKey}
          onLoadMore={loadMoreRedis}
          onCollapseAll={collapseRedis}
          onCloseInspect={() => {
            setRedisInspect(null)
            setRedisInspectKey("")
            setRedisInspectOpen(false)
          }}
          onInspectOpenChange={setRedisInspectOpen}
          onDeleteTargetChange={setRedisDeleteTarget}
          onDeleteOpenChange={setRedisDeleteOpen}
          onPrefixPreviewOpenChange={setRedisPrefixPreviewOpen}
          onPreviewPrefix={previewRedisPrefix}
          onConfirmDeleteKey={deleteRedisKey}
          onConfirmDeletePrefix={deleteRedisPrefix}
          redisAutoRefresh={redisAutoRefresh}
          redisAutoRefreshMs={redisAutoRefreshMs}
          onAutoRefreshChange={setRedisAutoRefresh}
          onAutoRefreshMsChange={setRedisAutoRefreshMs}
        />

        {treeCtxMenu ? (
          <TreeContextMenu
            menu={treeCtxMenu}
            onOpenData={() => {
              if (treeCtxMenu.table) {
                void selectTreeTable(treeCtxMenu.connId, treeCtxMenu.table, treeCtxMenu.database)
              }
            }}
            onQueryTable={() => {
              if (!treeCtxMenu.table) return
              const c = conns.find((x) => x.id === treeCtxMenu.connId)
              const q = c?.provider === "MySql" ? "`" : '"'
              setSql(`SELECT * FROM ${q}${treeCtxMenu.table}${q}`)
              setSelectedId(treeCtxMenu.connId)
              setRightTab("sql")
            }}
            onEditConnection={() => {
              const c = conns.find((x) => x.id === treeCtxMenu.connId)
              if (c) openEdit(c)
            }}
            onRefreshSchema={() => { void loadSchema(treeCtxMenu.connId) }}
            onDeleteConnection={() => {
              const c = conns.find((x) => x.id === treeCtxMenu.connId)
              if (c) setDeletingConn(c)
            }}
            onClose={() => setTreeCtxMenu(null)}
          />
        ) : null}

        {tabCtxMenu ? (
          <TabContextMenu
            menu={tabCtxMenu}
            onCloseTab={() => closeTab(tabCtxMenu.idx)}
            onCloseOtherTabs={() => closeOtherTabs(tabCtxMenu.idx)}
            onCloseAllTabs={closeAllTabs}
            onClose={() => setTabCtxMenu(null)}
          />
        ) : null}

        {cellCtxMenu && browseResult ? (
          <CellContextMenu
            menu={cellCtxMenu}
            column={browseResult.columns[cellCtxMenu.col] ?? ""}
            value={browseResult.rows[cellCtxMenu.row]?.[cellCtxMenu.col]}
            isDateTime={
              (activeTableSchema?.columns?.find((c) => c.name === browseResult.columns[cellCtxMenu.col])?.dataType ?? "")
                .toLowerCase()
                .includes("date") ||
              (activeTableSchema?.columns?.find((c) => c.name === browseResult.columns[cellCtxMenu.col])?.dataType ?? "")
                .toLowerCase()
                .includes("time")
            }
            isPrimaryKey={pkNames.includes(browseResult.columns[cellCtxMenu.col] ?? "")}
            mutating={mutating}
            selectedIsReadOnly={selectedIsReadOnly}
            fromCache={!!browseResult.fromCache}
            notifyDb={notifyDb}
            onCopyValue={() => {}}
            onSetNow={() => { void setCellValue(cellCtxMenu.row, cellCtxMenu.col, "now") }}
            onSetNull={() => { void setCellValue(cellCtxMenu.row, cellCtxMenu.col, null) }}
            onSetEmpty={() => { void setCellValue(cellCtxMenu.row, cellCtxMenu.col, "") }}
            onClose={() => setCellCtxMenu(null)}
          />
        ) : null}

        {showCreate ? (
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
            <div className="flex h-[34rem] max-h-[92vh] w-full max-w-2xl flex-col rounded-lg border border-border bg-background p-4 shadow-lg">
              <ConnectionForm
                provider={create.provider}
                mode={create.mode}
                host={create.host}
                port={create.port}
                database={create.database}
                username={create.username}
                password={create.password}
                filePath={create.filePath}
                connStr={create.connStr}
                title="New connection"
                environment={create.environment}
                portInvalid={createPortInvalid}
                ready={createReady}
                testing={createTesting}
                testResult={createTestResult}
                name={createEffectiveName}
                onNameChange={(value) =>
                  setCreate((prev) => ({ ...prev, name: value, nameEdited: value.trim() !== "" }))
                }
                onEnvironmentChange={(value) => setCreate((prev) => ({ ...prev, environment: value }))}
                onProviderChange={changeCreateProvider}
                onModeChange={(mode) => setCreate((prev) => ({ ...prev, mode }))}
                onHostChange={(value) => setCreate((prev) => ({ ...prev, host: value }))}
                onPortChange={(value) => setCreate((prev) => ({ ...prev, port: value }))}
                onDatabaseChange={(value) => setCreate((prev) => ({ ...prev, database: value }))}
                onUsernameChange={(value) => setCreate((prev) => ({ ...prev, username: value }))}
                onPasswordChange={(value) => setCreate((prev) => ({ ...prev, password: value }))}
                onFilePathChange={(value) => setCreate((prev) => ({ ...prev, filePath: value }))}
                onConnStrChange={(value) => setCreate((prev) => ({ ...prev, connStr: value }))}
                onAllowAgentMutationsChange={() => {}}
                onRequireApprovalChange={() => {}}
                onBrowseFile={() => { void openPicker("create") }}
                onTest={() => { void testConnectionCreate() }}
                onSubmit={() => { void submitCreate() }}
                onCancel={() => setShowCreate(false)}
                loadDatabases={loadCreateDatabases}
                serverKey={createServerKey}
              />
            </div>
          </div>
        ) : null}

        {edit ? (
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
            <div className="flex h-[34rem] max-h-[92vh] w-full max-w-2xl flex-col rounded-lg border border-border bg-background p-4 shadow-lg">
              <ConnectionForm
                provider={edit.provider}
                providerLocked
                passwordKeepsStored
                mode={edit.mode}
                host={edit.host}
                port={edit.port}
                database={edit.database}
                username={edit.username}
                password={edit.password}
                filePath={edit.filePath}
                connStr={edit.connStr}
                title={`Edit connection · ${edit.name}`}
                environment={edit.environment}
                portInvalid={editPortInvalid}
                ready={editReady}
                testing={editTesting}
                testResult={editTestResult}
                showMutationOptions
                allowAgentMutations={edit.allowAgentMutations}
                requireApprovalForMutations={edit.requireApprovalForMutations}
                name={edit.name}
                onNameChange={(value) => setEdit((prev) => (prev ? { ...prev, name: value } : prev))}
                onEnvironmentChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, environment: value } : prev))
                }
                onProviderChange={() => {}}
                onModeChange={(mode) => setEdit((prev) => (prev ? { ...prev, mode } : prev))}
                onHostChange={(value) => setEdit((prev) => (prev ? { ...prev, host: value } : prev))}
                onPortChange={(value) => setEdit((prev) => (prev ? { ...prev, port: value } : prev))}
                onDatabaseChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, database: value } : prev))
                }
                onUsernameChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, username: value } : prev))
                }
                onPasswordChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, password: value } : prev))
                }
                onFilePathChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, filePath: value } : prev))
                }
                onConnStrChange={(value) => setEdit((prev) => (prev ? { ...prev, connStr: value } : prev))}
                onAllowAgentMutationsChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, allowAgentMutations: value } : prev))
                }
                onRequireApprovalChange={(value) =>
                  setEdit((prev) => (prev ? { ...prev, requireApprovalForMutations: value } : prev))
                }
                onBrowseFile={() => { void openPicker("edit") }}
                onTest={() => { void testConnectionEdit() }}
                onSubmit={() => { void submitEdit() }}
                onCancel={closeEdit}
                loadDatabases={loadEditDatabases}
                serverKey={editServerKey}
              />
            </div>
          </div>
        ) : null}

        <HostPathPicker
          open={showPicker}
          mode="file"
          title="Select SQLite database file"
          onSelect={(path) => {
            // The manual form takes the path, the raw form takes a connection string.
            if (pickerTarget === "edit") {
              setEdit((prev) =>
                prev
                  ? prev.mode === "manual"
                    ? { ...prev, filePath: path }
                    : { ...prev, connStr: "Data Source=" + path }
                  : prev
              )
            } else {
              setCreate((prev) =>
                prev.mode === "manual"
                  ? { ...prev, filePath: path }
                  : { ...prev, connStr: "Data Source=" + path }
              )
            }
            setShowPicker(false)
          }}
          onClose={() => setShowPicker(false)}
        />

        <DeleteConnectionDialog
          conn={deletingConn}
          onConfirm={() => {
            if (deletingConn) void removeConnection(deletingConn.id)
            setDeletingConn(null)
          }}
          onCancel={() => setDeletingConn(null)}
        />

        <ImportConfirmDialog
          open={showImportConfirm}
          connections={detected.filter((d) => selectedKeys.includes(d.settingsKey))}
          onConfirm={() => { void doImport() }}
          onCancel={() => setShowImportConfirm(false)}
        />

        <DeleteRowConfirmDialog
          open={deleteRowTarget !== null}
          tableName={activeTable?.table ?? ""}
          mutating={mutating}
          onConfirm={() => { void confirmDeleteRow() }}
          onCancel={() => setDeleteRowTarget(null)}
        />

        <ReadOnlyDeleteDialog
          open={showReadOnlyDeleteDialog}
          onClose={() => setShowReadOnlyDeleteDialog(false)}
        />

        <NotificationStack notifications={notifications} onDismiss={dismissNotification} />
      </div>
    </div>
  )
}

