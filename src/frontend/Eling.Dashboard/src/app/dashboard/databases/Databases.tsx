/* eslint-disable react-hooks/immutability */
/* eslint-disable react-hooks/set-state-in-effect */
"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { Play, RefreshCw, Plus, AlertCircle, X, CheckCircle2 } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { copyText } from "@/lib/clipboard"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import { Table, TableHeader, TableBody, TableHead, TableRow, TableCell } from "@/components/ui/table"
import { DatePicker } from "@/components/ui/date-picker"
import { DatabaseCombobox } from "./DatabaseCombobox"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"

import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"

type DbConnDetails = {
  host: string | null
  port: number | null
  database: string | null
  username: string | null
  filePath: string | null
}

type DbConn = {
  id: string
  name: string
  provider: string
  environment: string
  hasConnectionString: boolean
  allowAgentMutations: boolean
  requireApprovalForMutations: boolean
  createdAt: string
  lastConnectedAt: string | null
  details?: DbConnDetails | null
  /** True when the connection names no database: it reaches every database on the server. */
  isServerLevel?: boolean
}

type QueryResult = {
  columns: string[]
  rows: unknown[][]
  truncated: boolean
  fromCache?: boolean
  cachedAt?: string | null
}

type ColumnSchema = { name: string; dataType: string; isNullable: boolean; isPrimaryKey: boolean }
type TableSchema = { name: string; kind: string; columns?: ColumnSchema[]; foreignKeys?: { column: string; referencesTable: string; referencesColumn: string }[] }
type SchemaInfo = { tables?: TableSchema[]; databases?: string[]; fromCache?: boolean; cachedAt?: string | null }

type Detected = {
  settingsKey: string
  name: string
  maskedConnectionString: string
  provider: string | null
  rejectionReason: string | null
  sourceFile?: string | null
}

const API = "/api/databases"

const formatCacheAge = (cachedAt?: string | null): string => {
  if (!cachedAt) return "cached"
  const ms = Date.now() - new Date(cachedAt).getTime()
  if (!Number.isFinite(ms) || ms < 0) return "cached"
  const sec = Math.floor(ms / 1000)
  if (sec < 60) return `${sec}s ago`
  const min = Math.floor(sec / 60)
  if (min < 60) return `${min} min ago`
  const hr = Math.floor(min / 60)
  if (hr < 24) return `${hr} h ago`
  const day = Math.floor(hr / 24)
  return `${day} day${day === 1 ? "" : "s"} ago`
}

const isStaleCache = (cachedAt?: string | null): boolean => {
  if (!cachedAt) return false
  const ms = Date.now() - new Date(cachedAt).getTime()
  return Number.isFinite(ms) && ms > 24 * 60 * 60 * 1000
}

const DEFAULT_MYSQL_PORT = 3306

/** The shape the form is in: type the fields, or paste a connection string. */
type ConnMode = "manual" | "raw"

/**
 * The name a connection gets when nobody typed one: `{database}@{host}` for
 * MySQL (with the port when it is not the default) and `{file}@local` for
 * SQLite. The same convention the import from appsettings uses.
 *
 * A MySQL connection with no database is a server-level connection, so its
 * name is just the host.
 */
const deriveConnectionName = (
  provider: string,
  host: string,
  port: string,
  database: string,
  filePath: string,
): string => {
  if (provider === "Sqlite") {
    const file = filePath.trim().split(/[\\/]/).filter(Boolean).pop() ?? ""
    const base = file.replace(/\.[^.]+$/, "")
    return base ? `${base}@local` : ""
  }
  const h = host.trim()
  if (!h) return ""
  const p = port.trim()
  const portSuffix = p && Number(p) !== DEFAULT_MYSQL_PORT ? `:${p}` : ""
  const db = database.trim()
  return db ? `${db}@${h}${portSuffix}` : `${h}${portSuffix}`
}

/**
 * The fields a person typed, in the shape the backend builds a string from.
 * A blank database is a server-level connection: the dashboard then lists
 * the databases on that server.
 */
const manualPayload = (
  provider: string,
  host: string,
  port: string,
  database: string,
  username: string,
  password: string,
  filePath: string,
) =>
  provider === "Sqlite"
    ? { filePath: filePath.trim() || null }
    : {
        host: host.trim() || null,
        port: port.trim() ? Number(port.trim()) : null,
        database: database.trim() || null,
        username: username.trim() || null,
        password: password.length > 0 ? password : null,
      }

/** True when the port is a number a socket can actually use. */
const isValidPort = (port: string): boolean => {
  const trimmed = port.trim()
  if (!trimmed) return true
  if (!/^\d+$/.test(trimmed)) return false
  const value = Number(trimmed)
  return value >= 1 && value <= 65535
}

/** A value short enough to sit next to its column name in a toast. */
const shortValue = (value: string): string =>
  value.length > 120 ? `${value.slice(0, 120)}…` : value

const parseApiError = (text: string): string => {
  const trimmed = text.trim()
  if (!trimmed) return "Request failed."
  try {
    const j = JSON.parse(trimmed)
    const msg = j?.detail || j?.title || j?.message || j?.error
    if (typeof msg === "string" && msg.length > 0) {
      // make common backend messages more human-friendly
      const lower = msg.toLowerCase()
      if (lower.includes("mutations not allowed on read-only")) {
        return "This connection is read-only, so data changes are disabled."
      }
      if (lower.includes("not a mutation statement")) {
        return "Only INSERT / UPDATE / DELETE statements are allowed here."
      }
      if (lower.includes("no primary key")) {
        return "This table has no primary key, so rows cannot be edited."
      }
      return msg
    }
  } catch {
    // not JSON — fall through to raw text
  }
  return trimmed.length > 200 ? trimmed.slice(0, 200) + "…" : trimmed
}

interface DbNotification {
  id: number
  type: "success" | "error" | "warning"
  title: string
  text?: string
}

const MAX_DB_NOTIFICATIONS = 4
const DB_NOTIFICATION_TTL_MS = 4000
let nextDbNotificationId = 0

function DbToastItem({
  notification,
  onDismiss,
}: {
  notification: DbNotification
  onDismiss: (id: number) => void
}) {
  useEffect(() => {
    const timer = setTimeout(() => onDismiss(notification.id), DB_NOTIFICATION_TTL_MS)
    return () => clearTimeout(timer)
  }, [notification.id, onDismiss])

  return (
    <div className="pointer-events-auto flex w-80 max-w-[calc(100vw-2rem)] items-start gap-2.5 rounded-lg border bg-background p-3 shadow-lg animate-in fade-in-0 slide-in-from-right-8 duration-300">
      <span
        className={
          notification.type === "error"
            ? "flex size-7 shrink-0 items-center justify-center rounded-full bg-destructive/15 text-destructive"
            : "flex size-7 shrink-0 items-center justify-center rounded-full bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
        }
      >
        {notification.type === "error" ? (
          <AlertCircle className="size-4" />
        ) : (
          <CheckCircle2 className="size-4" />
        )}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{notification.title}</p>
        {notification.text ? (
          <p className="mt-0.5 text-xs break-words text-muted-foreground">{notification.text}</p>
        ) : null}
      </div>
      <Button
        variant="ghost"
        size="icon"
        className="size-6 shrink-0"
        onClick={() => onDismiss(notification.id)}
        aria-label="Dismiss notification"
      >
        <X className="size-3.5" />
      </Button>
    </div>
  )
}

export function Databases() {
  const [conns, setConns] = useState<DbConn[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
 
  // create form
  const [showCreate, setShowCreate] = useState(false)
  const [cName, setCName] = useState("")
  const [cNameEdited, setCNameEdited] = useState(false)
  const [cProvider, setCProvider] = useState("Sqlite")
  const [cMode, setCMode] = useState<ConnMode>("manual")
  const [cConnStr, setCConnStr] = useState("")
  const [cHost, setCHost] = useState("")
  const [cPort, setCPort] = useState("")
  const [cDatabase, setCDatabase] = useState("")
  const [cUsername, setCUsername] = useState("")
  const [cPassword, setCPassword] = useState("")
  const [cFilePath, setCFilePath] = useState("")
  const [cEnv, setCEnv] = useState("ReadOnly")
  const [createTestResult, setCreateTestResult] = useState<{ ok: boolean; message: string; latencyMs: number } | null>(null)
  const [createTesting, setCreateTesting] = useState(false)

  // query
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [rightTab, setRightTab] = useState<"data" | "schema" | "sql">("data")
  const rightTabRef = useRef(rightTab)
  useEffect(() => { rightTabRef.current = rightTab }, [rightTab])
  const [deletingId, setDeletingId] = useState<string | null>(null)
  const [schemaConnId, setSchemaConnId] = useState<string | null>(null)
  const [schemaInfo, setSchemaInfo] = useState<SchemaInfo | null>(null)
  const [expandedConns, setExpandedConns] = useState<Set<string>>(new Set())
  const [connSchemas, setConnSchemas] = useState<Record<string, SchemaInfo>>({})
  const [activeTable, setActiveTable] = useState<{ connId: string; table: string; database?: string | null } | null>(null)
  // Open table tabs: clicking a table in the tree opens (or activates) a tab
  // here instead of replacing the single active table view.
  const [openTabs, setOpenTabs] = useState<Array<{ connId: string; table: string; database?: string | null }>>([])
  const [expandedDbs, setExpandedDbs] = useState<Set<string>>(new Set())
  const [dbSchemas, setDbSchemas] = useState<Record<string, SchemaInfo>>({})
  const treeItemRefs = useRef<Record<string, HTMLButtonElement | null>>({})
  const activeTabElRef = useRef<HTMLDivElement | null>(null)
  const asideRef = useRef<HTMLElement | null>(null)
  const connSchemasRef = useRef<Record<string, SchemaInfo>>({})
  const dbSchemasRef = useRef<Record<string, SchemaInfo>>({})
  const schemaConnIdRef = useRef<string | null>(null)
  const schemaRetryRef = useRef<Record<string, number>>({})
  const autoScrollPendingRef = useRef(false)
  const browsePollRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const dataScrollRef = useRef<HTMLDivElement | null>(null)
  const activeTableRef = useRef<{ connId: string; table: string; database?: string | null } | null>(null)
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; connId: string; table?: string; database?: string } | null>(null)
  const [cellCtxMenu, setCellCtxMenu] = useState<{ x: number; y: number; row: number; col: number } | null>(null)
  const [tabCtxMenu, setTabCtxMenu] = useState<{ x: number; y: number; idx: number } | null>(null)
  const [schemaLoading, setSchemaLoading] = useState(false)
  const [browseTable, setBrowseTable] = useState<string | null>(null)
  const [browseResult, setBrowseResult] = useState<QueryResult | null>(null)
  const [browseTotal, setBrowseTotal] = useState<number | null>(null)
  const [editingCell, setEditingCell] = useState<{ row: number; col: number } | null>(null)
  const [editValue, setEditValue] = useState("")
  const [savingCell, setSavingCell] = useState(false)
  const [newRow, setNewRow] = useState<Record<string, string> | null>(null)
  const [mutating, setMutating] = useState(false)
  const [deleteRowTarget, setDeleteRowTarget] = useState<number | null>(null)
  const [showReadOnlyDeleteDialog, setShowReadOnlyDeleteDialog] = useState(false)
  const [dbNotifications, setDbNotifications] = useState<DbNotification[]>([])

  const dismissDbNotification = useCallback((id: number) => {
    setDbNotifications((current) => current.filter((item) => item.id !== id))
  }, [])

  const notifyDb = useCallback((type: DbNotification["type"], title: string, text?: string) => {
    nextDbNotificationId += 1
    const id = nextDbNotificationId
    setDbNotifications((current) => [...current.slice(-(MAX_DB_NOTIFICATIONS - 1)), { id, type, title, text }])
  }, [])
  const [browseLoading, setBrowseLoading] = useState(false)
  const [editing, setEditing] = useState<DbConn | null>(null)
  const [eName, setEName] = useState("")
  const [eProvider, setEProvider] = useState("Sqlite")
  const [eMode, setEMode] = useState<ConnMode>("manual")
  const [eConnStr, setEConnStr] = useState("")
  const [eHost, setEHost] = useState("")
  const [ePort, setEPort] = useState("")
  const [eDatabase, setEDatabase] = useState("")
  const [eUsername, setEUsername] = useState("")
  const [ePassword, setEPassword] = useState("")
  const [eFilePath, setEFilePath] = useState("")
  const [eEnv, setEEnv] = useState("ReadOnly")
  const [eAllow, setEAllow] = useState(false)
  const [eRequire, setERequire] = useState(true)
  const [editTestResult, setEditTestResult] = useState<{ ok: boolean; message: string; latencyMs: number } | null>(null)
  const [editTesting, setEditTesting] = useState(false)
  const [testResults, setTestResults] = useState<Record<string, { ok: boolean; message: string; latencyMs: number } | undefined>>({})
  const [sql, setSql] = useState("SELECT 1")
  const [qResult, setQResult] = useState<QueryResult | null>(null)
  // qError removed - errors now shown via toast (notifyDb)
  const [qLoading, setQLoading] = useState(false)

  // True when the currently selected connection is read-only. In that case we
  // must NOT hit the mutate endpoint at all (avoids pointless network spam /
  // 403 round-trips) — surface the restriction locally instead.
  const selectedConn = conns.find((c) => c.id === selectedId)
  const selectedIsReadOnly = selectedConn?.environment === "ReadOnly"

  // The name follows the fields the person typed, until they type a name of
  // their own.
  const cEffectiveName = cNameEdited
    ? cName
    : deriveConnectionName(cProvider, cHost, cPort, cDatabase, cFilePath)
  const cPortInvalid = !isValidPort(cPort)
  const cReady = cMode === "raw"
    ? cConnStr.trim() !== "" && cEffectiveName.trim() !== ""
    : cProvider === "Sqlite"
      ? cFilePath.trim() !== "" && cEffectiveName.trim() !== ""
      : cHost.trim() !== "" && cEffectiveName.trim() !== ""

  const ePortInvalid = !isValidPort(ePort)
  const eReady = eMode === "raw"
    ? true
    : eProvider === "Sqlite"
      ? eFilePath.trim() !== ""
      : eHost.trim() !== ""


  // detect/import
  const [detected, setDetected] = useState<Detected[]>([])
  const [selectedKeys, setSelectedKeys] = useState<string[]>([])

  // sqlite file picker
  const [showPicker, setShowPicker] = useState(false)
  const [pickerPath, setPickerPath] = useState<string | null>(null)
  const [pickerParent, setPickerParent] = useState<string | null>(null)
  const [pickerEntries, setPickerEntries] = useState<{ path: string; isDirectory: boolean }[]>([])
  const [pickerLoading, setPickerLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const res = await fetch(API, { cache: "no-store" })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      const data = await res.json()
      setConns(data)
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "failed")
    } finally {
      setLoading(false)
    }
  }, [notifyDb])

  useEffect(() => {
    const id = setTimeout(() => { void load() }, 0)
    return () => clearTimeout(id)
  }, [load])

  // A connection added outside this tab — the agent's db_import_connections, a
  // second browser tab, or another eling process — writes the shared registry
  // file, and the backend broadcasts "databases" for it. Reload so the new
  // connection appears without anybody pressing Refresh.
  const reloadOnRegistryChange = useCallback(() => {
    void load()
  }, [load])
  useMemoriesSse(reloadOnRegistryChange, undefined, ["databases"])

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

      if (st?.expandedConns) setExpandedConns(new Set(st.expandedConns))
      if (Array.isArray(st?.openTabs)) setOpenTabs(st.openTabs)
      if (connId) {
        setSelectedId(connId)
        setSchemaConnId(connId)
        // ensure the connection holding the previously selected table is
        // expanded so its table row is rendered (and can be scrolled to)
        setExpandedConns((prev) => {
          const next = new Set(prev)
          next.add(connId)
          return next
        })
      }
      setRightTab(tab)

      const table = urlTable || st?.activeTable?.table
      const database = params.get("database") || st?.activeTable?.database || null
      if (connId && table) {
        autoScrollPendingRef.current = true
        const restored = { connId, table, database: database ?? null }
        setActiveTable(restored)
        setOpenTabs((prev) => prev.some((t) => t.connId === connId && t.table === table && (t.database ?? null) === (database ?? null)) ? prev : [...prev, restored])
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
          // background revalidate / fetch
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
  useEffect(() => { connSchemasRef.current = connSchemas }, [connSchemas])
  useEffect(() => { activeTableRef.current = activeTable }, [activeTable])
  useEffect(() => () => { if (browsePollRef.current) clearTimeout(browsePollRef.current) }, [])
  useEffect(() => { dbSchemasRef.current = dbSchemas }, [dbSchemas])
  useEffect(() => { schemaConnIdRef.current = schemaConnId }, [schemaConnId])

  // Auto-load schema for any expanded connection that doesn't have one yet
  // (covers restore / stale state where "Loading." would otherwise stick)
  useEffect(() => {
    for (const id of expandedConns) {
      if (!connSchemas[id]) void ensureSchema(id)
    }
  }, [expandedConns, connSchemas])

  // Persist explorer position (only after restore, to avoid clobbering saved state on mount)
  useEffect(() => {
    if (!restoredRef.current) return
    try {
      localStorage.setItem("db.explorer.state", JSON.stringify({
        expandedConns: Array.from(expandedConns),
        rightTab,
        selectedId,
        schemaConnId,
        activeTable,
        openTabs,
      }))
      const params = new URLSearchParams()
      if (schemaConnId) params.set("connectionId", schemaConnId)
      if (activeTable?.table) params.set("table", activeTable.table)
      if (activeTable?.database) params.set("database", activeTable.database)
      if (rightTab) params.set("tab", rightTab)
      const qs = params.toString()
      window.history.replaceState(null, "", qs ? "?" + qs : window.location.pathname)
    } catch {
      // ignore
    }
  }, [expandedConns, rightTab, selectedId, schemaConnId, activeTable, openTabs])

  /** The body the create form and the create test share. */
  const createPayload = () => {
    const body: Record<string, unknown> = {
      name: cEffectiveName.trim(),
      provider: cProvider,
      environment: cEnv,
      allowAgentMutations: false,
      requireApprovalForMutations: true,
    }
    if (cMode === "raw") {
      body.connectionString = cConnStr
    } else {
      body.manual = manualPayload(cProvider, cHost, cPort, cDatabase, cUsername, cPassword, cFilePath)
    }
    return body
  }

  /**
   * The databases on the server the create form points at. The credentials
   * are not saved yet, so this asks the backend to read them off the server
   * with what was just typed.
   */
  const loadCreateDatabases = async (): Promise<string[]> => {
    const res = await fetch(API + "/list-databases", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(createPayload()),
    })
    if (!res.ok) throw new Error(parseApiError(await res.text()))
    const data = await res.json()
    return Array.isArray(data) ? data : []
  }

  /**
   * The databases on the server the edited connection points at. The stored
   * password is not in the form, so the backend layers the entered fields
   * over the stored connection.
   */
  const loadEditDatabases = async (): Promise<string[]> => {
    if (!editing) return []
    const res = await fetch(API + "/" + editing.id + "/databases", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        manual: manualPayload(eProvider, eHost, ePort, eDatabase, eUsername, ePassword, eFilePath),
      }),
    })
    if (!res.ok) throw new Error(parseApiError(await res.text()))
    const data = await res.json()
    return Array.isArray(data) ? data : []
  }

  const resetCreateForm = () => {
    setCName("")
    setCNameEdited(false)
    setCMode("manual")
    setCConnStr("")
    setCHost("")
    setCPort("")
    setCDatabase("")
    setCUsername("")
    setCPassword("")
    setCFilePath("")
    setCreateTestResult(null)
  }

  const openCreate = () => {
    resetCreateForm()
    setShowCreate(true)
  }

  const onCreateProviderChange = (value: string | null) => {
    if (!value) return
    // The fields of the provider we are leaving do not apply to the one we
    // are switching to.
    if (value === "Sqlite") {
      setCHost("")
      setCPort("")
      setCDatabase("")
      setCUsername("")
      setCPassword("")
    } else {
      setCFilePath("")
    }
    setCProvider(value)
  }

  const create = async () => {
    if (cPortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    try {
      const res = await fetch(API, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(createPayload()),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      setShowCreate(false)
      resetCreateForm()
      await load()
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "create failed")
    }
  }

  const testConnectionCreate = async () => {
    if (cPortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    setCreateTesting(true)
    setCreateTestResult(null)
    try {
      const res = await fetch(API + "/test-connection", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(createPayload()),
      })
      const data = await res.json()
      setCreateTestResult(data)
    } catch (e: unknown) {
      setCreateTestResult({ ok: false, message: e instanceof Error ? e.message : "failed", latencyMs: 0 })
    } finally {
      setCreateTesting(false)
    }
  }

  const openEdit = (c: DbConn) => {
    setEditing(c)
    setEName(c.name)
    setEProvider(c.provider)
    setEConnStr("")
    setEEnv(c.environment)
    setEAllow(c.allowAgentMutations)
    setERequire(c.requireApprovalForMutations)
    setEditTestResult(null)
    // The stored connection is read back into fields so the form can be
    // pre-filled. The password is never part of that answer - it stays
    // blank, and a blank password keeps the stored one.
    const details = c.details ?? null
    setEHost(details?.host ?? "")
    setEPort(details?.port != null ? String(details.port) : "")
    setEDatabase(details?.database ?? "")
    setEUsername(details?.username ?? "")
    setEPassword("")
    setEFilePath(details?.filePath ?? "")
    setEMode(details ? "manual" : "raw")
  }

  const saveEdit = async () => {
    if (!editing) return
    if (ePortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    const body: Record<string, unknown> = {
      name: eName,
      environment: eEnv,
      allowAgentMutations: eAllow,
      requireApprovalForMutations: eRequire,
    }
    if (eMode === "raw") {
      if (eConnStr.trim() !== "") body.connectionString = eConnStr
    } else {
      body.manual = manualPayload(eProvider, eHost, ePort, eDatabase, eUsername, ePassword, eFilePath)
    }
    try {
      const res = await fetch(API + "/" + editing.id, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      setEditing(null)
      await load()
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "update failed")
    }
  }

  const testConnectionEdit = async () => {
    if (!editing) return
    if (ePortInvalid) {
      notifyDb("error", "Port must be a number between 1 and 65535.")
      return
    }
    setEditTesting(true)
    setEditTestResult(null)
    try {
      let res: Response
      if (eMode === "raw") {
        if (eConnStr.trim() !== "") {
          res = await fetch(API + "/test-connection", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              name: eName || editing.name,
              provider: eProvider,
              connectionString: eConnStr,
              environment: eEnv,
              allowAgentMutations: eAllow,
              requireApprovalForMutations: eRequire,
            }),
          })
        } else {
          res = await fetch(API + "/" + editing.id + "/test", { method: "POST" })
        }
      } else {
        // The edited fields are layered over the stored connection on the
        // server, so a blank password keeps the stored one.
        res = await fetch(API + "/" + editing.id + "/test", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            manual: manualPayload(eProvider, eHost, ePort, eDatabase, eUsername, ePassword, eFilePath),
          }),
        })
      }
      const data = await res.json()
      setEditTestResult(data)
    } catch (e: unknown) {
      setEditTestResult({ ok: false, message: e instanceof Error ? e.message : "failed", latencyMs: 0 })
    } finally {
      setEditTesting(false)
    }
  }

  const loadSchema = async (id: string) => {
    setSchemaConnId(id)
    setSchemaLoading(true)
    setSchemaInfo(null)
    setBrowseTable(null)
    setBrowseResult(null)
    setBrowseTotal(null)
    try {
      const data = await fetchSchema(id)
      setSchemaInfo(data)
      setConnSchemas((prev) => ({ ...prev, [id]: data }))
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "schema failed")
    } finally {
      setSchemaLoading(false)
    }
  }

  const fetchSchema = async (id: string, database?: string | null) => {
    const qs = database ? "?database=" + encodeURIComponent(database) : ""
    const res = await fetch(API + "/" + id + "/schema" + qs)
    if (!res.ok) throw new Error(parseApiError(await res.text()))
    return await res.json()
  }

  const ensureSchema = useCallback(async (id: string, opts?: { force?: boolean }) => {
    const force = opts?.force ?? false
    if (!force && connSchemasRef.current[id]?.fromCache === false) return

    try {
      const fresh = await fetchSchema(id)
      setConnSchemas((prev) => ({ ...prev, [id]: fresh }))
      if (schemaConnIdRef.current === id) setSchemaInfo(fresh)
      schemaRetryRef.current[id] = 0
      if (fresh.fromCache) {
        const delay = (schemaRetryRef.current[id] ?? 0) < 12 ? 5000 : 15000
        schemaRetryRef.current[id] = (schemaRetryRef.current[id] ?? 0) + 1
        setTimeout(() => { void ensureSchema(id, { force: true }) }, delay)
      }
    } catch {
      const retries = schemaRetryRef.current[id] ?? 0
      if (retries < 3) {
        schemaRetryRef.current[id] = retries + 1
        setTimeout(() => { void ensureSchema(id) }, 2000 * (retries + 1))
      }
    }
  }, [])

  // The tables of one database on a server-level connection. The database
  // name is part of the cache key, so two databases never share a schema.
  const ensureDbSchema = useCallback(async (connId: string, database: string) => {
    const key = `${connId}:${database}`
    if (dbSchemasRef.current[key] && !dbSchemasRef.current[key]?.fromCache) return

    try {
      const fresh = await fetchSchema(connId, database)
      setDbSchemas((prev) => ({ ...prev, [key]: fresh }))
      if (fresh.fromCache) {
        setTimeout(() => { void ensureDbSchema(connId, database) }, 5000)
      }
    } catch (e: unknown) {
      notifyDb("error", e instanceof Error ? e.message : "schema failed")
    }
  }, [notifyDb])

  const toggleConn = (id: string) => {
    const wasExpanded = expandedConns.has(id)
    setExpandedConns((prev) => {
      const next = new Set(prev)
      if (wasExpanded) next.delete(id)
      else next.add(id)
      return next
    })
    if (!wasExpanded) void ensureSchema(id)
  }

  const toggleDb = (connId: string, database: string) => {
    const key = `${connId}:${database}`
    const wasExpanded = expandedDbs.has(key)
    setExpandedDbs((prev) => {
      const next = new Set(prev)
      if (wasExpanded) next.delete(key)
      else next.add(key)
      return next
    })
    if (!wasExpanded) void ensureDbSchema(connId, database)
  }

  const selectTreeTable = async (connId: string, table: string, database?: string | null) => {
    autoScrollPendingRef.current = false
    setError(null)
    const newActive = { connId, table, database: database ?? null }
    // Open (or reuse) a tab for this table so multiple tables can stay open.
    setOpenTabs((prev) => {
      const exists = prev.some((t) => t.connId === connId && t.table === table && (t.database ?? null) === (database ?? null))
      if (exists) return prev
      return [...prev, newActive]
    })
    setActiveTable(newActive)
    activeTableRef.current = newActive
    setSelectedId(connId)
    setSchemaConnId(connId)
    setBrowseTable(table)
    // preserve current tab instead of forcing "data"
    // schema: use in-memory, then localStorage cache, then fetch
    const schema: SchemaInfo | null = database
      ? dbSchemasRef.current[`${connId}:${database}`] ?? null
      : connSchemasRef.current[connId] ?? null
    // (localStorage schema cache removed - backend is the source)
    if (schema) {
      if (database) {
        setDbSchemas((prev) => (prev[`${connId}:${database}`] ? prev : { ...prev, [`${connId}:${database}`]: schema }))
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
  }

  const activateTab = (idx: number) => {
    const t = openTabs[idx]
    if (!t) return
    // Auto-scroll the tree to show the selected table.
    autoScrollPendingRef.current = true
    // Reuse selectTreeTable to load schema/data for the tab.
    void selectTreeTable(t.connId, t.table, t.database ?? undefined)
  }

  const closeTab = (idx: number) => {
    setOpenTabs((prev) => {
      const next = prev.filter((_, i) => i !== idx)
      // If we closed the active tab, activate a neighbor.
      const closed = prev[idx]
      const wasActive = activeTable && closed && activeTable.connId === closed.connId && activeTable.table === closed.table && (activeTable.database ?? null) === (closed.database ?? null)
      if (wasActive) {
        const neighbor = next[Math.min(idx, next.length - 1)]
        if (neighbor) {
          // Defer activation to after state update.
          setTimeout(() => { autoScrollPendingRef.current = true; void selectTreeTable(neighbor.connId, neighbor.table, neighbor.database ?? undefined) }, 0)
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
  }

  const closeOtherTabs = (idx: number) => {
    setOpenTabs((prev) => {
      const keep = prev[idx]
      if (!keep) return prev
      // Activate the kept tab if it isn't already active.
      if (!(activeTable && activeTable.connId === keep.connId && activeTable.table === keep.table && (activeTable.database ?? null) === (keep.database ?? null))) {
        setTimeout(() => { autoScrollPendingRef.current = true; void selectTreeTable(keep.connId, keep.table, keep.database ?? undefined) }, 0)
      }
      return [keep]
    })
  }

  const closeAllTabs = () => {
    setOpenTabs([])
    setActiveTable(null)
    activeTableRef.current = null
    setBrowseTable(null)
    setBrowseResult(null)
    setBrowseTotal(null)
  }

  const browseData = async (id: string, table: string, database?: string | null, background = false) => {
    // Guard against stale polls from a previous table selection.
    const current = activeTableRef.current
    if (current && (current.connId !== id || current.table !== table)) return
    if (!background) {
      setError(null)
      setEditingCell(null)
    }
    // Preserve scroll position across refreshes so the table doesn't jump.
    const savedScroll = dataScrollRef.current?.scrollTop ?? 0
    const isNewTable = !background && browseTable !== table
    setBrowseTable(table)
    if (!background) setBrowseLoading(true)
    if (isNewTable) {
      if (browsePollRef.current) clearTimeout(browsePollRef.current)
      browsePollRef.current = null
      setBrowseResult(null)
      setBrowseTotal(null)
    }
    try {
      const conn = conns.find((c) => c.id === id)
      const isMySql = conn?.provider === "MySql"
      const quote = isMySql ? "`" : '"'
      const escaped = table.replace(new RegExp(quote, "g"), quote + quote)
      const sql = "SELECT * FROM " + quote + escaped + quote + " LIMIT 100"
      const [dataRes, countRes] = await Promise.all([
        fetch(API + "/" + id + "/query", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sql, maxRows: 100, database: database ?? null }),
        }),
        fetch(API + "/" + id + "/query", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sql: "SELECT COUNT(*) FROM " + quote + escaped + quote, maxRows: 1, database: database ?? null }),
        }),
      ])
      if (!dataRes.ok) throw new Error(parseApiError(await dataRes.text()))
      const data = await dataRes.json() as QueryResult
      setBrowseResult(data)
      if (countRes.ok) {
        const cd = await countRes.json() as QueryResult
        const v = cd?.rows?.[0]?.[0]
        if (v !== null && v !== undefined) {
          const n = typeof v === "number" ? v : Number(v)
          if (Number.isFinite(n)) setBrowseTotal(n)
        }
      }
      if (browsePollRef.current) clearTimeout(browsePollRef.current)
      if (data.fromCache) {
        // No toast here: the in-page "Cached data" banner already conveys it,
        // and a toast on every 5s poll (plus lingering after reconnect) is noisy.
        browsePollRef.current = setTimeout(() => { void browseData(id, table, database, true) }, 5000)
      } else {
        browsePollRef.current = null
      }
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : "browse failed"
      const conn = conns.find((c) => c.id === id)
      notifyDb("error", `Could not load ${table}`, `${conn?.name ?? id}: ${msg}`)
    } finally {
      setBrowseLoading(false)
      // Restore scroll position so refresh doesn't jump the table.
      requestAnimationFrame(() => {
        if (dataScrollRef.current) dataScrollRef.current.scrollTop = savedScroll
      })
    }
  }

  const activeSchemaTable = () => schemaInfo?.tables?.find((t) => t.name === activeTable?.table)

  const pkColumnNames = () => (activeSchemaTable()?.columns?.filter((c) => c.isPrimaryKey).map((c) => c.name) ?? [])

  const quoteIdent = (name: string) => {
    const isMySql = conns.find((c) => c.id === selectedId)?.provider === "MySql"
    const q = isMySql ? "`" : '"'
    return q + name.replace(new RegExp(q, "g"), q + q) + q
  }

  const sqlQuote = (v: unknown) => {
    if (v === null || v === undefined) return "NULL"
    if (typeof v === "number" && Number.isFinite(v)) return String(v)
    if (typeof v === "boolean") return v ? "1" : "0"
    const s = String(v)
    // keep unquoted for values that are pure integers/floats coming through as strings
    if (/^-?\d+(\.\d+)?$/.test(s)) return s
    return "'" + s.replace(/'/g, "''") + "'"
  }

  const refreshData = async () => {
    if (!selectedId || !activeTable) return
    await browseData(selectedId, activeTable.table, activeTable.database)
  }

  const buildWherePk = (row: unknown[]) => {
    const cols = browseResult?.columns ?? []
    const pks = pkColumnNames()
    if (pks.length === 0) return ""
    return pks.map((pk) => {
      const idx = cols.indexOf(pk)
      const v = row[idx]
      return `${pk} = ${sqlQuote(v)}`
    }).join(" AND ")
  }

  const startEdit = (rowIndex: number, colIndex: number, value: unknown) => {
    if (pkColumnNames().length === 0) return
    setEditingCell({ row: rowIndex, col: colIndex })
    setEditValue(String(value ?? ""))
  }

  const commitEdit = async (overrideValue?: string) => {
    if (!editingCell || !browseResult || !selectedId || !activeTable) return
    if (selectedIsReadOnly) { setEditingCell(null); return }
    const { row: ri, col: ci } = editingCell
    const colName = browseResult.columns[ci]
    if (pkColumnNames().includes(colName)) {
      // editing PK not allowed in v1
      setEditingCell(null)
      return
    }
    const where = buildWherePk(browseResult.rows[ri])
    if (!where) { setEditingCell(null); return }
    const val = overrideValue !== undefined ? overrideValue : editValue
    const sqlStmt = `UPDATE ${quoteIdent(activeTable.table)} SET ${colName} = ${sqlQuote(val)} WHERE ${where}`
    const prevValue = browseResult.rows[ri][ci]
    // optimistic apply
    setBrowseResult((prev) => {
      if (!prev) return prev
      const rows = prev.rows.map((r, i) => (i === ri ? r.map((v, j) => (j === ci ? val : v)) : r))
      return { ...prev, rows }
    })
    setEditingCell(null)
    setSavingCell(true)
    try {
      const res = await fetch(API + "/" + selectedId + "/mutate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: sqlStmt, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      await refreshData()
    } catch (e: unknown) {
      // revert
      setBrowseResult((prev) => {
        if (!prev) return prev
        const rows = prev.rows.map((r, i) => (i === ri ? r.map((v, j) => (j === ci ? prevValue : v)) : r))
        return { ...prev, rows }
      })
      setError(e instanceof Error ? e.message : "update failed")
    } finally {
      setSavingCell(false)
    }
  }

  const cancelEdit = () => setEditingCell(null)

  // A column is "date-like" when its declared type mentions date/time. The
  // schema drives the DatePicker in the grid, so the context menu follows the
  // same rule for the "Set now" action.
  const isDateTimeColumn = (colName: string): boolean => {
    const dt = (activeSchemaTable()?.columns?.find((c) => c.name === colName)?.dataType ?? "").toLowerCase()
    return dt.includes("date") || dt.includes("time")
  }

  // Set a single cell to NULL, empty string, or "now" (date/time columns) via
  // the mutate endpoint. Uses the row's primary key as the WHERE clause, so it
  // targets exactly one row.
  const setCellValue = async (rowIndex: number, colIndex: number, value: null | "" | "now") => {
    if (!browseResult || !selectedId || !activeTable) return
    if (selectedIsReadOnly) { notifyDb("error", "This connection is read-only, so data changes are disabled."); return }
    if (browseResult?.fromCache) { notifyDb("error", "This data is cached (database unreachable), so edits are disabled."); return }
    const colName = browseResult.columns[colIndex]
    if (pkColumnNames().includes(colName)) { notifyDb("error", "Primary key columns cannot be changed here."); return }
    const colSchema = activeSchemaTable()?.columns?.find((c) => c.name === colName)
    if (value === null && colSchema && !colSchema.isNullable) { notifyDb("error", `Column ${colName} is NOT NULL.`); return }
    const where = buildWherePk(browseResult.rows[rowIndex])
    if (!where) { notifyDb("error", "no primary key — cannot update"); return }

    // Build the SQL literal plus the value shown optimistically in the grid.
    let literal: string
    let display: unknown
    if (value === null) {
      literal = "NULL"
      display = null
    } else if (value === "") {
      literal = "''"
      display = ""
    } else {
      const now = new Date()
      const pad = (n: number) => String(n).padStart(2, "0")
      const datePart = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
      const timePart = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`
      const withTime = (colSchema?.dataType ?? "").toLowerCase().includes("time")
      display = withTime ? `${datePart}T${timePart}` : datePart
      literal = sqlQuote(display)
    }

    const sqlStmt = `UPDATE ${quoteIdent(activeTable.table)} SET ${quoteIdent(colName)} = ${literal} WHERE ${where}`
    // optimistic apply
    setBrowseResult((prev) => {
      if (!prev) return prev
      const rows = prev.rows.map((r, i) => (i === rowIndex ? r.map((v, j) => (j === colIndex ? display : v)) : r))
      return { ...prev, rows }
    })
    setMutating(true)
    try {
      const res = await fetch(API + "/" + selectedId + "/mutate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: sqlStmt, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      await refreshData()
      notifyDb("success", value === null ? `Set ${colName} to null` : value === "" ? `Cleared ${colName}` : `Set ${colName} to now`)
    } catch (e: unknown) {
      await refreshData()
      setError(e instanceof Error ? e.message : "update failed")
    } finally {
      setMutating(false)
    }
  }

  const promptDeleteRow = (rowIndex: number) => {
    if (selectedIsReadOnly || browseResult?.fromCache) return
    if (!browseResult || !selectedId || !activeTable) return
    const where = buildWherePk(browseResult.rows[rowIndex])
    if (!where) { notifyDb("error", "no primary key — cannot delete"); return }
    setDeleteRowTarget(rowIndex)
  }

  const confirmDeleteRow = async () => {
    if (deleteRowTarget === null || !browseResult || !selectedId || !activeTable) return
    if (selectedIsReadOnly) { setDeleteRowTarget(null); return }
    const rowIndex = deleteRowTarget
    const where = buildWherePk(browseResult.rows[rowIndex])
    if (!where) { notifyDb("error", "no primary key — cannot delete"); setDeleteRowTarget(null); return }
    // optimistic removal
    setBrowseResult((prev) => {
      if (!prev) return prev
      return { ...prev, rows: prev.rows.filter((_, i) => i !== rowIndex) }
    })
    setDeleteRowTarget(null)
    setMutating(true)
    try {
      const sqlStmt = `DELETE FROM ${quoteIdent(activeTable.table)} WHERE ${where}`
      const res = await fetch(API + "/" + selectedId + "/mutate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: sqlStmt, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      const data = await res.json().catch(() => ({ affectedRows: 0, truncated: false }))
      await refreshData()
      if ((data?.affectedRows ?? 0) === 0) {
        setError("Nothing was deleted — no matching row.")
      } else {
        notifyDb("success", `Deleted ${data.affectedRows} row${data.affectedRows === 1 ? "" : "s"}`)
      }
    } catch (e: unknown) {
      await refreshData()
      setError(e instanceof Error ? e.message : "delete failed")
    } finally {
      setMutating(false)
      setDeleteRowTarget(null)
    }
  }

  const submitNewRow = async () => {
    if (!newRow || !browseResult || !selectedId || !activeTable) return
    if (selectedIsReadOnly) { return }
    const cols = browseResult.columns
    const pks = pkColumnNames()
    const colList = cols.map((c) => quoteIdent(c)).join(", ")
    const valList = cols.map((c) => {
      const v = newRow[c] ?? ""
      if (pks.includes(c) && v === "") return "NULL"
      return sqlQuote(v)
    }).join(", ")
    const sqlStmt = `INSERT INTO ${quoteIdent(activeTable.table)} (${colList}) VALUES (${valList})`
    const optimisticRow = cols.map((c) => (newRow[c] ?? ""))
    // optimistic apply
    setBrowseResult((prev) => {
      if (!prev) return prev
      return { ...prev, rows: [...prev.rows, optimisticRow] }
    })
    setMutating(true)
    try {
      const res = await fetch(API + "/" + selectedId + "/mutate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: sqlStmt, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      setNewRow(null)
      await refreshData()
    } catch (e: unknown) {
      // revert
      setBrowseResult((prev) => {
        if (!prev) return prev
        return { ...prev, rows: prev.rows.slice(0, -1) }
      })
      setError(e instanceof Error ? e.message : "insert failed")
    } finally {
      setMutating(false)
    }
  }

  const remove = async (id: string) => {
    await fetch(API + "/" + id, { method: "DELETE" })
    await load()
  }

  const testConn = async (id: string) => {
    setTestResults((prev) => ({ ...prev, [id]: undefined }))
    try {
      const res = await fetch(API + "/" + id + "/test", { method: "POST" })
      const data = await res.json()
      setTestResults((prev) => ({ ...prev, [id]: data }))
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : "failed"
      setTestResults((prev) => ({ ...prev, [id]: { ok: false, message: msg, latencyMs: 0 } }))
    }
  }

  const runQuery = async () => {
    if (!selectedId) return
    setQLoading(true); setQResult(null)
    try {
      const trimmed = sql.trim()
      if (/^\s*(insert|update|delete|create|alter|drop|truncate|replace)\b/i.test(trimmed)) {
        if (selectedIsReadOnly) { notifyDb("error", "Read-only connection", "This connection is read-only, so data changes are disabled."); setQLoading(false); return }
        const res = await fetch(API + "/" + selectedId + "/mutate", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sql: trimmed, maxAffectedRows: 1000, database: activeTable?.database ?? null }),
        })
        if (!res.ok) throw new Error(parseApiError(await res.text()))
        const data = await res.json()
        setQResult({ columns: ["affectedRows", "truncated"], rows: [[data.affectedRows, data.truncated]], truncated: false })
        return
      }
      let finalSql = trimmed
      if (!/\blimit\b/i.test(finalSql)) {
        finalSql = finalSql.replace(/;?\s*$/, "") + " LIMIT 100"
      }
      const res = await fetch(API + "/" + selectedId + "/query", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: finalSql, maxRows: 100, database: activeTable?.database ?? null }),
      })
      if (!res.ok) throw new Error(parseApiError(await res.text()))
      const qData = await res.json() as QueryResult
      setQResult(qData)
      if (qData.fromCache) {
        const conn = conns.find((c) => c.id === selectedId)
        notifyDb("warning", `Cached result`, `${conn?.name ?? selectedId}: database unavailable, showing snapshot from ${formatCacheAge(qData.cachedAt)}`)
      }
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : "query failed"
      const conn = conns.find((c) => c.id === selectedId)
      notifyDb("error", `Query failed`, `${conn?.name ?? selectedId}: ${msg}`)
    } finally {
      setQLoading(false)
    }
  }

  const detect = async () => {
    const res = await fetch(API + "/detect", { cache: "no-store" })
    setDetected(await res.json())
  }

  const doImport = async () => {
    const res = await fetch(API + "/import", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ settingsKeys: selectedKeys }),
    })
    await res.json()
    setSelectedKeys([])
    setDetected([])
    await load()
  }

  const browse = async (path: string | null) => {
    setPickerLoading(true)
    try {
      if (!path) {
        const res = await fetch("/api/agent/host/drives")
        const drives: string[] = await res.json()
        setPickerPath(null)
        setPickerParent(null)
        setPickerEntries(drives.map((d) => ({ path: d, isDirectory: true })))
      } else {
        const res = await fetch("/api/agent/host/browse?path=" + encodeURIComponent(path))
        const data = await res.json()
        setPickerPath(data.path)
        setPickerParent(data.parent)
        setPickerEntries(data.entries ?? [])
      }
    } catch {
      setPickerEntries([])
    } finally {
      setPickerLoading(false)
    }
  }

  const [pickerTarget, setPickerTarget] = useState<"create" | "edit">("create")

  const openPicker = async (target: "create" | "edit" = "create") => {
    setPickerTarget(target)
    setShowPicker(true)
    try {
      const res = await fetch("/api/agent/host/drives")
      const drives: string[] = await res.json()
      if (drives && drives.length > 0) {
        browse(drives[0])
      } else {
        browse(null)
      }
    } catch {
      browse(null)
    }
  }

   return (
     <div className="flex h-screen flex-col overflow-hidden">
       <div className="shrink-0">
         <DashboardHeader label="Databases" />
       </div>
       <div className="flex-1 flex flex-col min-h-0 overflow-hidden gap-4 p-4 pt-2">
         <div className="flex flex-wrap gap-2 shrink-0">
        <Button onClick={() => { if (showCreate) { setShowCreate(false) } else { openCreate() } }}>
          <Plus className="mr-2 size-4" /> New connection
        </Button>

        <Button variant="outline" onClick={() => { void load(); for (const id of expandedConns) void ensureSchema(id, { force: true }) }}>
          <RefreshCw className="mr-2 size-4" /> Refresh
        </Button>
      </div>


      {error ? <p className="text-sm text-destructive">{error}</p> : null}


      {detected.length > 0 ? (
        <Card>
          <CardHeader><CardTitle>Detected connections</CardTitle><CardDescription>Passwords masked. Select keys to import (read-only). Every appsettings*.json file in the workspace contributes its own keys.</CardDescription></CardHeader>
          <CardContent className="space-y-2">
            {detected.map(d => (
              <label key={d.settingsKey} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={selectedKeys.includes(d.settingsKey)}
                  onChange={e => {
                    if (e.target.checked) setSelectedKeys([...selectedKeys, d.settingsKey])
                    else setSelectedKeys(selectedKeys.filter(k => k !== d.settingsKey))
                  }}
                  disabled={d.provider === null}
                />
                <span className="font-medium">{d.settingsKey}</span>
                {d.sourceFile ? <span className="text-xs text-muted-foreground">{d.sourceFile}</span> : null}
                <span className="text-muted-foreground">→ {d.name}</span>
                <span className="text-xs text-muted-foreground">{d.maskedConnectionString}</span>
                {d.rejectionReason ? <Badge variant="destructive">{d.rejectionReason}</Badge> : null}
              </label>
            ))}
            <Button onClick={doImport} disabled={selectedKeys.length === 0}>Import selected</Button>
          </CardContent>
        </Card>
      ) : null}

       <div className="flex flex-1 min-h-0 gap-4 overflow-hidden">
        <aside ref={(el) => { asideRef.current = el }} className="w-72 shrink-0 min-h-0 overflow-y-auto">
      <Card>
        <CardHeader><CardTitle>Connections</CardTitle></CardHeader>
        <CardContent className="space-y-1 p-2">
          {loading ? <p className="text-sm text-muted-foreground">Loading.</p> : null}
          {conns.map(c => (
            <div key={c.id} className="space-y-1">
              <button
                className="flex w-full items-center gap-1 rounded px-1 py-1 text-left text-sm hover:bg-accent"
                onClick={() => toggleConn(c.id)}
                onContextMenu={(e) => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, connId: c.id }) }}
              >
                <span className="text-xs">{expandedConns.has(c.id) ? "▾" : "▸"}</span>
                <span className="truncate font-medium">{c.name}</span>
              </button>
              {expandedConns.has(c.id) ? (
                <div className="ml-3 space-y-0.5">
                  {!connSchemas[c.id] ? <p className="ml-2 text-xs text-muted-foreground">Loading.</p> : null}
                  {c.isServerLevel ? (
                    // A server-level connection reaches every database on the
                    // server, so the tree lists them before the tables.
                    (connSchemas[c.id]?.databases ?? []).map((db) => (
                      <div key={`${c.id}:${db}`} className="space-y-0.5">
                        <button
                          className="flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent"
                          onClick={() => toggleDb(c.id, db)}
                        >
                          <span className="text-xs">{expandedDbs.has(`${c.id}:${db}`) ? "▾" : "▸"}</span>
                          <span className="truncate font-medium">{db}</span>
                        </button>
                        {expandedDbs.has(`${c.id}:${db}`) ? (
                          <div className="ml-3 space-y-0.5">
                            {(dbSchemas[`${c.id}:${db}`]?.tables ?? []).map((t) => (
                              <button
                                key={`${c.id}:${db}:${t.name}`}
                                ref={(el) => { treeItemRefs.current[`${c.id}:${db}:${t.name}`] = el }}
                                className={`flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent ${activeTable?.connId === c.id && activeTable?.table === t.name && activeTable?.database === db ? "bg-accent font-medium" : ""}`}
                                onClick={() => selectTreeTable(c.id, t.name, db)}
                                onContextMenu={(e) => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, connId: c.id, table: t.name, database: db }) }}
                              >
                                <span>📄</span>
                                <span className="truncate">{t.name}</span>
                              </button>
                            ))}
                          </div>
                        ) : null}
                      </div>
                    ))
                  ) : (
                    (connSchemas[c.id]?.tables ?? []).map((t) => (
                      <button
                        key={t.name}
                        ref={(el) => { treeItemRefs.current[`${c.id}:${t.name}`] = el }}
                        className={`flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent ${activeTable?.connId === c.id && activeTable?.table === t.name ? "bg-accent font-medium" : ""}`}
                        onClick={() => selectTreeTable(c.id, t.name)}
                        onContextMenu={(e) => { e.preventDefault(); setCtxMenu({ x: e.clientX, y: e.clientY, connId: c.id, table: t.name }) }}
                      >
                        <span>📄</span>
                        <span className="truncate">{t.name}</span>
                      </button>
                    ))
                  )}

                </div>
              ) : null}
            </div>
          ))}
          {conns.length === 0 && !loading ? <p className="text-sm text-muted-foreground">No connections.</p> : null}
        </CardContent>
      </Card>

        </aside>
        <main className="flex-1 min-h-0 space-y-4 overflow-hidden">
      {openTabs.length > 0 ? (
        <div className="flex items-center gap-1 border-b pb-2 overflow-x-auto">
          {openTabs.map((t, idx) => {
            const isActive = activeTable?.connId === t.connId && activeTable?.table === t.table && (activeTable?.database ?? null) === (t.database ?? null)
            return (
              <div ref={isActive ? activeTabElRef : undefined} key={`${t.connId}:${t.database ?? ""}:${t.table}`} className={`flex items-center gap-1 rounded px-2 py-1 text-xs cursor-pointer border ${isActive ? "bg-accent font-medium border-accent" : "hover:bg-accent/50 border-transparent"}`} onClick={() => activateTab(idx)} onContextMenu={(e) => { e.preventDefault(); setTabCtxMenu({ x: e.clientX, y: e.clientY, idx }) }}>
                <span className="truncate max-w-[140px]">{t.table}</span>
                <button className="ml-1 text-muted-foreground hover:text-foreground" onClick={(e) => { e.stopPropagation(); closeTab(idx) }}>×</button>
              </div>
            )
          })}
        </div>
      ) : null}
      <div className="flex gap-2 border-b pb-2">
        <Button size="sm" variant={rightTab === "data" ? "default" : "outline"} onClick={() => setRightTab("data")}>Data</Button>
        <Button size="sm" variant={rightTab === "schema" ? "default" : "outline"} onClick={() => setRightTab("schema")}>Schema</Button>
        <Button size="sm" variant={rightTab === "sql" ? "default" : "outline"} onClick={() => setRightTab("sql")}>SQL</Button>
      </div>
      {rightTab === "schema" && schemaConnId ? (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              Schema · {conns.find((c) => c.id === schemaConnId)?.name}
              {schemaInfo?.fromCache ? (
                <Badge variant="outline" className={isStaleCache(schemaInfo.cachedAt) ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400" : "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"}>
                  {isStaleCache(schemaInfo.cachedAt) ? "stale" : "cached"} · {formatCacheAge(schemaInfo.cachedAt)}
                </Badge>
              ) : null}
            </CardTitle>
          </CardHeader>
          <CardContent>
            {schemaLoading ? <p className="text-sm text-muted-foreground">Loading…</p> : null}
            {(() => {
              const t = activeTable ? schemaInfo?.tables?.find((x) => x.name === activeTable.table) : null
              if (t) {
                return (
                  <div className="space-y-3">
                    <p className="font-medium">{t.name} <span className="text-xs text-muted-foreground">({t.kind})</span></p>
                    <div className="overflow-x-auto">
                      <table className="w-full text-xs">
                        <thead>
                          <tr>
                            <th className="border px-2 py-1 text-left">Column</th>
                            <th className="border px-2 py-1 text-left">Type</th>
                            <th className="border px-2 py-1 text-left">Nullable</th>
                            <th className="border px-2 py-1 text-left">PK</th>
                          </tr>
                        </thead>
                        <tbody>
                          {t.columns?.map((col) => (
                            <tr key={col.name}>
                              <td className="border px-2 py-1">{col.name}</td>
                              <td className="border px-2 py-1">{col.dataType}</td>
                              <td className="border px-2 py-1">{col.isNullable ? "yes" : "no"}</td>
                              <td className="border px-2 py-1">{col.isPrimaryKey ? "✓" : ""}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    {t.foreignKeys?.length ? (
                      <p className="text-xs text-muted-foreground">
                        FKs: {t.foreignKeys.map((f) => `${f.column}→${f.referencesTable}.${f.referencesColumn}`).join(", ")}
                      </p>
                    ) : null}
                  </div>
                )
              }
              if (schemaInfo?.tables?.length) {
                return (
                  <div className="space-y-1">
                    <p className="text-xs text-muted-foreground">Click a table to view its schema.</p>
                    {schemaInfo.tables.map((tbl) => (
                      <button
                        key={tbl.name}
                        className="flex w-full items-center gap-2 rounded border px-3 py-2 text-left text-sm hover:bg-accent"
                        onClick={() => { setActiveTable({ connId: schemaConnId!, table: tbl.name }); setBrowseTable(tbl.name); setRightTab("schema") }}
                      >
                        <span>📄</span>
                        <span className="font-medium">{tbl.name}</span>
                        <span className="text-xs text-muted-foreground">({tbl.kind})</span>
                      </button>
                    ))}
                  </div>
                )
              }
              return null
            })()}


            <div className="mt-3">
              <Button variant="outline" size="sm" onClick={() => setSchemaConnId(null)}>Close</Button>
            </div>
          </CardContent>
        </Card>
      ) : null}

      {rightTab === "data" && !activeTable ? (
        <p className="text-sm text-muted-foreground">Select a table from the tree to view its data.</p>
      ) : null}
      {rightTab === "data" && activeTable ? (
        <>
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
              Data - {activeTable.table}
              {browseResult?.fromCache ? (
                <Badge variant="outline" className={isStaleCache(browseResult.cachedAt) ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400" : "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"}>
                  {isStaleCache(browseResult.cachedAt) ? "stale" : "cached"} · {formatCacheAge(browseResult.cachedAt)}
                </Badge>
              ) : null}
            </CardTitle>
            </CardHeader>
          <CardContent>
            {/* banner + toolbar: always visible */}
            <div className="space-y-2">
              {selectedIsReadOnly && !browseResult?.fromCache ? <p className="text-xs text-muted-foreground">Read-only connection - cells can be edited, but never persisted.</p> : null}
              {browseResult?.fromCache ? <p className="text-xs text-amber-700 dark:text-amber-400">Cached data - the database is unreachable, so edits are disabled.</p> : null}
              <div className="flex items-center gap-3 mb-2">
                  <Button size="sm" variant="outline" onClick={() => { void refreshData() }} disabled={browseLoading || mutating} title="Refresh table data">
                    <RefreshCw className={browseLoading ? "size-4 animate-spin" : "size-4"} />
                    <span className="ml-1">Refresh</span>
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => browseResult && setNewRow(Object.fromEntries(browseResult.columns.map((c) => [c, ""])))} title={selectedIsReadOnly ? "read-only connection - editing disabled" : (pkColumnNames().length === 0 ? "no primary key - read only" : undefined)} disabled={mutating || pkColumnNames().length === 0 || selectedIsReadOnly || !!browseResult?.fromCache || !browseResult}>
                    + Add row
                  </Button>
                  {pkColumnNames().length === 0 ? <span className="text-xs text-muted-foreground">No primary key - read only</span> : null}
                </div>
            </div>
            {/* table: data or loading */}
            <div className="space-y-2">
              {browseResult ? (
                <>
                  <Table ref={dataScrollRef} className="text-sm" containerClassName="h-[calc(100vh-280px)] rounded-md border">
                    <TableHeader>
                      <TableRow className="sticky top-0 z-20 bg-background">
                        {browseResult.columns.map((col) => {
  const isIdCol = /^id$/i.test(col)
  const colSchema = activeSchemaTable()?.columns?.find((c) => c.name === col)
  const dt = (colSchema?.dataType ?? "").toLowerCase()
  const isInt = isIdCol && (dt ? /int|serial|bigint|smallint/.test(dt) : (browseResult.rows.length > 0 && browseResult.rows.every((r) => { const v = r[browseResult.columns.indexOf(col)]; return v === null || v === undefined || v === "" || /^-?\d+$/.test(String(v)) })))
  return <TableHead key={col} className={`border px-2 py-1 text-left ${isIdCol ? "w-px whitespace-nowrap" : ""} ${isInt ? "sticky left-0 bg-background z-40 shadow-[2px_0_4px_-2px_rgba(0,0,0,0.15)]" : ""}`}>{col}</TableHead>
})}
                        <TableHead className="border px-2 py-1 text-left w-16 sticky right-0 bg-background z-40">act</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {browseResult.rows.map((row, i: number) => (
                        <TableRow key={i}>
                          {row.map((v, j: number) => {
                            const colName = browseResult.columns[j]
                            const isPk = pkColumnNames().includes(colName)
                            const isEditing = editingCell?.row === i && editingCell?.col === j
                            const isIdCol = /^id$/i.test(colName)
                            const idColSchema = activeSchemaTable()?.columns?.find((c) => c.name === colName)
                            const idColType = (idColSchema?.dataType ?? "").toLowerCase()
                            const isIdInt = isIdCol && (idColType ? /int|serial|bigint|smallint/.test(idColType) : (browseResult.rows.length > 0 && browseResult.rows.every((r) => { const v = r[j]; return v === null || v === undefined || v === "" || /^-?\d+$/.test(String(v)) })))
                            if (isEditing) {
                              const colType = (activeSchemaTable()?.columns?.find((c) => c.name === colName)?.dataType ?? "").toLowerCase()
                              const isDate = colType.includes("date") || colType.includes("time")
                              if (isDate && !selectedIsReadOnly) {
                                return (
                                  <TableCell key={j} className="border px-1 py-0.5">
                                    <DatePicker
                                      value={editValue}
                                      withTime={colType.includes("time")}
                                      onChange={(v) => { setEditValue(v) }}
                                    />
                                  </TableCell>
                                )
                              }
                              return (
                                <TableCell key={j} className="border px-1 py-0.5">
                                  <div className="flex items-center gap-1">
                                    <input
                                      type="text"
                                      className="w-full rounded border px-1 py-0.5 text-sm"
                                      value={editValue}
                                      autoFocus
                                      onChange={(e) => setEditValue(e.target.value)}
                                      onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void commitEdit() } if (e.key === "Escape") cancelEdit() }}
                                      disabled={savingCell}
                                      readOnly={selectedIsReadOnly}
                                    />
                                    {!selectedIsReadOnly ? <button type="button" className="shrink-0 rounded border px-1.5 py-0.5 text-xs text-green-600 hover:bg-accent" onClick={() => { void commitEdit() }} disabled={savingCell || mutating || !!browseResult?.fromCache} title="Apply edit">Apply</button> : null}
                                  </div>
                                </TableCell>
                              )
                            }
                            return (
                              <TableCell
                                key={j}
                                className={`border px-2 py-1 whitespace-nowrap ${/^id$/i.test(colName) ? "w-px" : ""} ${isPk && !isIdInt ? "bg-muted/40" : ""} ${!isPk ? "cursor-pointer hover:bg-accent/40" : ""} ${isIdInt ? `sticky left-0 z-10 ${isPk ? "bg-muted" : "bg-background"} shadow-[2px_0_4px_-2px_rgba(0,0,0,0.15)]` : ""}`}
                                title={v === null || v === undefined ? "null" : String(v)}
                                onClick={() => { if (!isPk && pkColumnNames().length > 0) startEdit(i, j, v) }}
                                onContextMenu={(e) => {
                                  e.preventDefault()
                                  e.stopPropagation()
                                  // keep the menu inside the viewport
                                  const menuW = 200
                                  const menuH = 200
                                  const x = Math.min(e.clientX, window.innerWidth - menuW - 8)
                                  const y = Math.min(e.clientY, window.innerHeight - menuH - 8)
                                  setCellCtxMenu({ x: Math.max(8, x), y: Math.max(8, y), row: i, col: j })
                                }}
                              >
                                {v === null || v === undefined ? (
                                  <span className="text-muted-foreground/70 italic">null</span>
                                ) : (
                                  String(v)
                                )}
                              </TableCell>
                            )
                          })}
                          <TableCell className="border px-2 py-1 sticky right-0 bg-background z-10">
                            {editingCell?.row === i ? (
                              <div className="flex gap-1">
                                {!selectedIsReadOnly ? (
                                  <button
                                    className="text-xs text-green-600 hover:underline"
                                    onClick={() => { void commitEdit() }}
                                    disabled={savingCell || mutating || !!browseResult?.fromCache}
                                  >
                                    apply
                                  </button>
                                ) : null}
                                <button
                                  className="text-xs text-muted-foreground hover:underline"
                                  onClick={() => cancelEdit()}
                                  disabled={savingCell}
                                >
                                  cancel
                                </button>
                              </div>
                            ) : (
                              <button
                                className="text-xs text-destructive hover:underline"
                                onClick={() => { if (selectedIsReadOnly) setShowReadOnlyDeleteDialog(true); else promptDeleteRow(i) }}
                                title={selectedIsReadOnly ? "read-only connection - delete disabled" : (pkColumnNames().length === 0 ? "no primary key - read only" : undefined)}
                                disabled={mutating || pkColumnNames().length === 0}
                              >
                                del
                              </button>
                            )}
                          </TableCell>
                        </TableRow>
                      ))}
                      {newRow ? (
                        <TableRow>
                          {browseResult.columns.map((col) => {
                            const colType = (activeSchemaTable()?.columns?.find((c) => c.name === col)?.dataType ?? "").toLowerCase()
                            const isDate = colType.includes("date") || colType.includes("time")
                            if (isDate) {
                              return (
                                <TableCell key={col} className="border px-1 py-0.5">
                                  <DatePicker
                                    value={newRow[col] ?? ""}
                                    withTime={colType.includes("time")}
                                    onChange={(v) => setNewRow((prev) => prev ? { ...prev, [col]: v } : prev)}
                                  />
                                </TableCell>
                              )
                            }
                            return (
                              <TableCell key={col} className="border px-1 py-0.5">
                                <input
                                  className="w-full rounded border px-1 py-0.5 text-sm"
                                  value={newRow[col] ?? ""}
                                  onChange={(e) => setNewRow((prev) => prev ? { ...prev, [col]: e.target.value } : prev)}
                                />
                              </TableCell>
                            )
                          })}
                          <TableCell className="border px-2 py-1 flex gap-1">
                            <button className="text-xs text-green-600 hover:underline" onClick={() => { void submitNewRow() }} disabled={mutating}>save</button>
                            <button className="text-xs text-muted-foreground hover:underline" onClick={() => setNewRow(null)}>cancel</button>
                          </TableCell>
                        </TableRow>
                      ) : null}
                    </TableBody>
                  </Table>
                  {browseResult.truncated ? <p className="mt-1 text-xs text-muted-foreground">Truncated.</p> : null}
                </>
              ) : (
                <Table className="text-sm" containerClassName="h-[calc(100vh-280px)] rounded-md border">
                  <TableHeader>
                    <TableRow className="sticky top-0 z-20 bg-background">
                      {(schemaInfo?.tables?.find((t) => t.name === activeTable?.table)?.columns ?? []).map((col) => (
                        <TableHead key={col.name} className="border px-2 py-1 text-left">{col.name}</TableHead>
                      ))}
                      <TableHead className="border px-2 py-1 text-left w-16 sticky right-0 bg-background z-40">act</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    <TableRow>
                      <TableCell colSpan={(schemaInfo?.tables?.find((t) => t.name === activeTable?.table)?.columns?.length ?? 0) + 1} className="text-center text-sm text-muted-foreground py-8">
                        Loading...
                      </TableCell>
                    </TableRow>
                  </TableBody>
                </Table>
              )}
            </div>
          </CardContent>
        </Card>
        <div className="sticky bottom-0 z-20 flex items-center gap-2 rounded-md bg-muted/80 px-3 py-1.5 text-xs font-medium text-muted-foreground backdrop-blur">
          {browseResult
            ? (browseTotal !== null
                ? `Total rows: ${browseTotal} (showing ${browseResult.rows.length}${browseResult.truncated ? ", truncated" : ""})`
                : `Total rows: ${browseResult.rows.length}`)
            : "Total rows: -"}
        </div>
        </>
      ) : null}
      {rightTab === "sql" && selectedId ? (
        <Card>
          <CardHeader><CardTitle className="flex items-center gap-2">Query - {conns.find(c => c.id === selectedId)?.name}{qResult?.fromCache ? <Badge variant="outline" className={isStaleCache(qResult.cachedAt) ? "border-orange-500/40 bg-orange-500/10 text-orange-700 dark:text-orange-400" : "border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400"}>{isStaleCache(qResult.cachedAt) ? "stale" : "cached"} · {formatCacheAge(qResult.cachedAt)}</Badge> : null}</CardTitle></CardHeader>
          <CardContent className="space-y-3">
            <textarea className="w-full rounded border p-2 text-sm font-mono" value={sql} onChange={e => setSql(e.target.value)} rows={4} />
            <Button onClick={runQuery} disabled={qLoading}>
              <Play className="mr-2 size-4" /> {qLoading ? "Running…" : "Run"}
            </Button>
            {qResult ? (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr>{qResult.columns.map(col => <th key={col} className="border px-2 py-1 text-left whitespace-nowrap">{col}</th>)}</tr>
                  </thead>
                  <tbody>
                    {qResult.rows.map((row, i) => (
                      <tr key={i}>{row.map((v, j) => <td key={j} className="border px-2 py-1 whitespace-nowrap">{String(v ?? "")}</td>)}</tr>
                    ))}
                  </tbody>
                </table>
                {qResult.truncated ? <p className="mt-1 text-xs text-muted-foreground">Truncated.</p> : null}
              </div>
            ) : null}
            </CardContent>
          </Card>
        ) : null}
        </main>
      </div>

      {ctxMenu ? (
        <div className="fixed inset-0 z-50" onClick={() => setCtxMenu(null)}>
          <div
            className="absolute min-w-[160px] rounded-md border border-border bg-popover p-1 shadow-md"
            style={{ left: ctxMenu.x, top: ctxMenu.y }}
            onClick={(e) => e.stopPropagation()}
          >
            {ctxMenu.table ? (
              <>
                <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { selectTreeTable(ctxMenu.connId, ctxMenu.table!, ctxMenu.database); setCtxMenu(null) }}>Open data</button>
                <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { const c = conns.find((x) => x.id === ctxMenu.connId); const q = c?.provider === "MySql" ? "`" : '"'; setSql(`SELECT * FROM ${q}${ctxMenu.table}${q}`); setSelectedId(ctxMenu.connId); setRightTab("sql"); setCtxMenu(null) }}>Query table</button>
              </>
            ) : null}
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { const c = conns.find((x) => x.id === ctxMenu.connId); if (c) openEdit(c); setCtxMenu(null) }}>Edit connection</button>
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { loadSchema(ctxMenu.connId); setCtxMenu(null) }}>Refresh schema</button>
            <button className="block w-full rounded px-2 py-1 text-left text-sm text-destructive hover:bg-accent" onClick={() => { setDeletingId(ctxMenu.connId); setCtxMenu(null) }}>Delete connection</button>
          </div>
        </div>
      ) : null}

      {tabCtxMenu ? (
        <div className="fixed inset-0 z-50" onClick={() => setTabCtxMenu(null)}>
          <div
            className="absolute min-w-[160px] rounded-md border border-border bg-popover p-1 shadow-md"
            style={{ left: tabCtxMenu.x, top: tabCtxMenu.y }}
            onClick={(e) => e.stopPropagation()}
          >
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { closeTab(tabCtxMenu.idx); setTabCtxMenu(null) }}>Close</button>
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { closeOtherTabs(tabCtxMenu.idx); setTabCtxMenu(null) }}>Close others</button>
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { closeAllTabs(); setTabCtxMenu(null) }}>Close all</button>
          </div>
        </div>
      ) : null}

      {cellCtxMenu ? (
        <div
          className="fixed inset-0 z-50"
          onClick={() => setCellCtxMenu(null)}
          onContextMenu={(e) => { e.preventDefault(); setCellCtxMenu(null) }}
        >
          <div
            className="absolute min-w-[160px] rounded-md border border-border bg-popover p-1 shadow-md"
            style={{ left: cellCtxMenu.x, top: cellCtxMenu.y }}
            onClick={(e) => e.stopPropagation()}
          >
            <p className="px-2 py-1 text-xs text-muted-foreground">
              {browseResult?.columns[cellCtxMenu.col] ?? ""}
            </p>
            <button
              className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent"
              title="Copy this cell's value to the clipboard"
              onClick={() => {
                const { row, col } = cellCtxMenu
                setCellCtxMenu(null)
                const column = browseResult?.columns[col] ?? ""
                const raw = browseResult?.rows[row]?.[col]
                // Copy what the cell shows, so what lands on the clipboard is
                // what the reader saw - a null cell copies as "null".
                const text = raw === null || raw === undefined ? "null" : String(raw)
                void copyText(text).then((ok) => {
                  if (ok) {
                    notifyDb("success", "Copied", `${column}: ${shortValue(text)}`)
                  } else {
                    notifyDb("error", "Copy failed", "The browser blocked clipboard access.")
                  }
                })
              }}
            >
              Copy value
            </button>
            <div className="my-1 h-px bg-border" />
            {isDateTimeColumn(browseResult?.columns[cellCtxMenu.col] ?? "") ? (
              <button
                className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent disabled:opacity-50 disabled:hover:bg-transparent"
                disabled={mutating || selectedIsReadOnly || !!browseResult?.fromCache || pkColumnNames().includes(browseResult?.columns[cellCtxMenu.col] ?? "")}
                title={selectedIsReadOnly ? "read-only connection — changes are disabled" : undefined}
                onClick={() => { const { row, col } = cellCtxMenu; setCellCtxMenu(null); void setCellValue(row, col, "now") }}
              >
                Set now
              </button>
            ) : null}
            <button
              className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent disabled:opacity-50 disabled:hover:bg-transparent"
              disabled={mutating || selectedIsReadOnly || !!browseResult?.fromCache || pkColumnNames().includes(browseResult?.columns[cellCtxMenu.col] ?? "")}
              title={selectedIsReadOnly ? "read-only connection — changes are disabled" : undefined}
              onClick={() => { const { row, col } = cellCtxMenu; setCellCtxMenu(null); void setCellValue(row, col, null) }}
            >
              Set null
            </button>
            <button
              className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent disabled:opacity-50 disabled:hover:bg-transparent"
              disabled={mutating || selectedIsReadOnly || !!browseResult?.fromCache || pkColumnNames().includes(browseResult?.columns[cellCtxMenu.col] ?? "")}
              onClick={() => { const { row, col } = cellCtxMenu; setCellCtxMenu(null); void setCellValue(row, col, "") }}
            >
              Set empty string
            </button>
          </div>
        </div>
      ) : null}

      {showPicker ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
          <div className="w-full max-w-2xl rounded-lg border border-border bg-background p-4 shadow-lg">
            <div className="mb-3 flex items-center justify-between">
              <p className="font-medium">Select SQLite database file</p>
              <Button variant="ghost" size="sm" onClick={() => setShowPicker(false)}>Close</Button>
            </div>
            <p className="mb-2 truncate text-xs text-muted-foreground">{pickerPath ?? "Drives"}</p>
            <div className="mb-2 flex gap-2">
              <Button variant="outline" size="sm" onClick={() => browse(null)}>
                Drives
              </Button>
              {pickerParent ? (
                <Button variant="outline" size="sm" onClick={() => browse(pickerParent)}>
                  .. Up
                </Button>
              ) : null}
            </div>
            {pickerLoading ? (
              <p className="text-sm text-muted-foreground">Loading…</p>
            ) : (
              <div className="h-[480px] overflow-y-auto rounded border">
                {pickerEntries.map((e) => (
                  <button
                    key={e.path}
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-accent"
                    onClick={() => {
                      if (e.isDirectory) {
                        browse(e.path)
                      } else {
                        // The manual form takes the path, the raw form takes a
                        // connection string.
                        if (pickerTarget === "edit") {
                          if (eMode === "manual") setEFilePath(e.path)
                          else setEConnStr("Data Source=" + e.path)
                        } else {
                          if (cMode === "manual") setCFilePath(e.path)
                          else setCConnStr("Data Source=" + e.path)
                        }
                        setShowPicker(false)
                      }
                    }}
                  >
                    <span>{e.isDirectory ? "📁" : "📄"}</span>
                    <span className="truncate">{e.path.split(/[\\\\/]/).pop()}</span>
                  </button>
                ))}
                {pickerEntries.length === 0 ? (
                  <p className="px-3 py-2 text-sm text-muted-foreground">Empty.</p>
                ) : null}
              </div>
            )}
          </div>
        </div>
      ) : null}

      {editing ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
          <div className="flex h-[34rem] max-h-[92vh] w-full max-w-2xl flex-col rounded-lg border border-border bg-background p-4 shadow-lg">
            <div className="mb-3 flex shrink-0 items-center justify-between">
              <p className="font-medium">Edit connection · {editing.name}</p>
              <Button variant="ghost" size="sm" onClick={() => setEditing(null)}>Close</Button>
            </div>
            <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1 pt-0.5">
              <Input placeholder="name" value={eName} onChange={e => setEName(e.target.value)} />
              {/* The provider cannot change on an existing connection, so it
                  sits beside the mode toggle instead of taking a row of its
                  own - the form then fits without scrolling. */}
              <div className="flex flex-wrap items-center gap-2">
                <Input value={eProvider} disabled className="w-32" />
                <div className="flex rounded-lg border p-0.5 w-fit">
                  <Button
                    size="sm"
                    variant={eMode === "manual" ? "secondary" : "ghost"}
                    onClick={() => setEMode("manual")}
                  >
                    Manual
                  </Button>
                  <Button
                    size="sm"
                    variant={eMode === "raw" ? "secondary" : "ghost"}
                    onClick={() => setEMode("raw")}
                  >
                    Raw string
                  </Button>
                </div>
              </div>
              {eMode === "manual" ? (
                eProvider === "Sqlite" ? (
                  <div className="flex gap-2">
                    <Input
                      placeholder="database file"
                      value={eFilePath}
                      onChange={e => setEFilePath(e.target.value)}
                      className="flex-1"
                    />
                    <Button type="button" variant="outline" onClick={() => openPicker("edit")}>Browse</Button>
                  </div>
                ) : (
                  <div className="grid gap-2 sm:grid-cols-2">
                    <Input placeholder="host" value={eHost} onChange={e => setEHost(e.target.value)} />
                    <Input
                      placeholder="port (3306)"
                      value={ePort}
                      onChange={e => setEPort(e.target.value)}
                      aria-invalid={ePortInvalid}
                    />
                    <Input placeholder="username" value={eUsername} onChange={e => setEUsername(e.target.value)} />
                    <Input
                      type="password"
                      placeholder="password (leave empty to keep the stored one)"
                      value={ePassword}
                      onChange={e => setEPassword(e.target.value)}
                    />
                    {/* Last, because the list it offers comes from the server
                        this connection reaches. */}
                    <DatabaseCombobox
                      value={eDatabase}
                      onChange={setEDatabase}
                      loadDatabases={loadEditDatabases}
                      serverKey={`${eProvider}|${eHost}|${ePort}|${eFilePath}|${eUsername}`}
                      placeholder="database (optional - pick or type)"
                      className="sm:col-span-2"
                    />
                  </div>
                )
              ) : (
                <div className="flex gap-2">
                  <Input
                    placeholder="new connection string (leave empty to keep stored)"
                    value={eConnStr}
                    onChange={e => setEConnStr(e.target.value)}
                    className="flex-1"
                  />
                  <Button type="button" variant="outline" onClick={() => openPicker("edit")}>Browse</Button>
                </div>
              )}
              {ePortInvalid ? (
                <p className="text-xs text-destructive">Port must be a number between 1 and 65535.</p>
              ) : null}
              <Select value={eEnv} onValueChange={(val) => { if (val) setEEnv(val) }}>
                <SelectTrigger className="w-full">
                  <SelectValue placeholder="Environment" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Development">Development</SelectItem>
                  <SelectItem value="Staging">Staging</SelectItem>
                  <SelectItem value="Production">Production</SelectItem>
                  <SelectItem value="ReadOnly">ReadOnly</SelectItem>
                </SelectContent>
              </Select>
              <div className="flex flex-wrap gap-x-6 gap-y-1">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={eAllow} onChange={e => setEAllow(e.target.checked)} />
                  Allow agent mutations
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={eRequire} onChange={e => setERequire(e.target.checked)} />
                  Require approval for mutations
                </label>
              </div>
              {editTestResult ? (
                <p className={`text-xs ${editTestResult.ok ? "text-green-600" : "text-destructive"}`}>
                  {editTestResult.ok ? "OK" : "FAIL"} {editTestResult.message} ({editTestResult.latencyMs}ms)
                </p>
              ) : null}
            </div>
            <div className="mt-3 flex shrink-0 gap-2">
              <Button onClick={saveEdit} disabled={!eReady}>Save</Button>
              <Button variant="outline" onClick={testConnectionEdit} disabled={editTesting || !eReady}>
                {editTesting ? "Testing…" : "Test"}
              </Button>
              <Button variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
            </div>
          </div>
        </div>
      ) : null}
{showCreate ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4">
          <div className="flex h-[34rem] max-h-[92vh] w-full max-w-2xl flex-col rounded-lg border border-border bg-background p-4 shadow-lg">
            <div className="mb-3 flex shrink-0 items-center justify-between">
              <p className="font-medium">New connection</p>
              <Button variant="ghost" size="sm" onClick={() => setShowCreate(false)}>Close</Button>
            </div>
            <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1 pt-0.5">
              <Input
                placeholder="name (auto-filled from database@host)"
                value={cEffectiveName}
                onChange={e => { setCName(e.target.value); setCNameEdited(e.target.value.trim() !== "") }}
              />
              <div className="flex flex-wrap items-center gap-2">
                <Select value={cProvider} onValueChange={onCreateProviderChange}>
                  <SelectTrigger className="w-40">
                    <SelectValue placeholder="Provider" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="Sqlite">Sqlite</SelectItem>
                    <SelectItem value="MySql">MySql</SelectItem>
                  </SelectContent>
                </Select>
                <div className="flex rounded-lg border p-0.5">
                  <Button
                    size="sm"
                    variant={cMode === "manual" ? "secondary" : "ghost"}
                    onClick={() => setCMode("manual")}
                  >
                    Manual
                  </Button>
                  <Button
                    size="sm"
                    variant={cMode === "raw" ? "secondary" : "ghost"}
                    onClick={() => setCMode("raw")}
                  >
                    Raw string
                  </Button>
                </div>
              </div>

              {cMode === "manual" ? (
                cProvider === "Sqlite" ? (
                  <div className="flex gap-2">
                    <Input
                      placeholder="database file"
                      value={cFilePath}
                      onChange={e => setCFilePath(e.target.value)}
                      className="flex-1"
                    />
                    <Button type="button" variant="outline" onClick={() => openPicker("create")}>
                      Browse
                    </Button>
                  </div>
                ) : (
                  <div className="grid gap-2 sm:grid-cols-2">
                    <Input placeholder="host" value={cHost} onChange={e => setCHost(e.target.value)} />
                    <Input
                      placeholder="port (3306)"
                      value={cPort}
                      onChange={e => setCPort(e.target.value)}
                      aria-invalid={cPortInvalid}
                    />
                    <Input placeholder="username" value={cUsername} onChange={e => setCUsername(e.target.value)} />
                    <Input
                      type="password"
                      placeholder="password"
                      value={cPassword}
                      onChange={e => setCPassword(e.target.value)}
                    />
                    {/* Last, because the list it offers comes from the server
                        these credentials reach. */}
                    <DatabaseCombobox
                      value={cDatabase}
                      onChange={setCDatabase}
                      loadDatabases={loadCreateDatabases}
                      serverKey={`${cProvider}|${cHost}|${cPort}|${cFilePath}|${cUsername}`}
                      placeholder="database (optional - pick or type)"
                      className="sm:col-span-2"
                    />
                  </div>
                )
              ) : (
                <div className="flex gap-2">
                  <Input
                    placeholder={cProvider === "Sqlite" ? "Data Source path or connection string" : "connection string"}
                    value={cConnStr}
                    onChange={e => setCConnStr(e.target.value)}
                    className="flex-1"
                  />
                  {cProvider === "Sqlite" ? (
                    <Button type="button" variant="outline" onClick={() => openPicker("create")}>
                      Browse
                    </Button>
                  ) : null}
                </div>
              )}
              {cPortInvalid ? (
                <p className="text-xs text-destructive">Port must be a number between 1 and 65535.</p>
              ) : null}
              <Select value={cEnv} onValueChange={(val) => { if (val) setCEnv(val) }}>
                <SelectTrigger className="w-full">
                  <SelectValue placeholder="Environment" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Development">Development</SelectItem>
                  <SelectItem value="Staging">Staging</SelectItem>
                  <SelectItem value="Production">Production</SelectItem>
                  <SelectItem value="ReadOnly">ReadOnly</SelectItem>
                </SelectContent>
              </Select>
              {createTestResult ? (
                <p className={`text-xs ${createTestResult.ok ? "text-green-600" : "text-destructive"}`}>
                  {createTestResult.ok ? "OK" : "FAIL"} {createTestResult.message} ({createTestResult.latencyMs}ms)
                </p>
              ) : null}
            </div>
            <div className="mt-3 flex shrink-0 gap-2">
              <Button onClick={create} disabled={!cReady}>Create</Button>
              <Button variant="outline" onClick={testConnectionCreate} disabled={createTesting || !cReady}>
                {createTesting ? "Testing…" : "Test"}
              </Button>
              <Button variant="outline" onClick={() => setShowCreate(false)}>Cancel</Button>
            </div>
          </div>
        </div>
      ) : null}

      {deletingId ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
          <div className="w-full max-w-sm rounded-lg border border-border bg-background p-4 shadow-lg">
            <p className="mb-3 font-medium">Delete connection?</p>
            <p className="mb-4 text-sm text-muted-foreground">
              {conns.find((c) => c.id === deletingId)?.name ?? deletingId} will be removed. This cannot be undone.
            </p>
            <div className="flex justify-end gap-2">
              <Button variant="outline" onClick={() => setDeletingId(null)}>Cancel</Button>
              <Button
                variant="destructive"
                onClick={async () => {
                  await remove(deletingId)
                  setDeletingId(null)
                }}
              >
                Delete
              </Button>
            </div>
          </div>
        </div>
      ) : null}

      <AlertDialog
        open={showReadOnlyDeleteDialog}
        onOpenChange={(open) => {
          if (!open) setShowReadOnlyDeleteDialog(false)
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Read-only connection</AlertDialogTitle>
            <AlertDialogDescription>
              This connection is read-only, so rows cannot be deleted.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogAction onClick={() => setShowReadOnlyDeleteDialog(false)}>
              OK
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog
        open={deleteRowTarget !== null}
        onOpenChange={(open) => {
          if (!open) setDeleteRowTarget(null)
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle className="text-destructive">Delete row?</AlertDialogTitle>
            <AlertDialogDescription>
              This will permanently delete the selected row from{" "}
              <span className="font-medium">{activeTable?.table}</span>. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={mutating}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={(e) => {
                e.preventDefault()
                void confirmDeleteRow()
              }}
              disabled={mutating}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {mutating ? "Deleting..." : "Delete"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <div className="pointer-events-none fixed top-4 right-4 z-[100] flex flex-col gap-2">
        {dbNotifications.map((n) => (
          <DbToastItem key={n.id} notification={n} onDismiss={dismissDbNotification} />
        ))}
      </div>

       </div>
     </div>
  )
}

