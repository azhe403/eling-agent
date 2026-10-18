/* eslint-disable react-hooks/immutability */
/* eslint-disable react-hooks/set-state-in-effect */
"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { Play, RefreshCw, Plus } from "lucide-react"

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

import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"

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
}

type QueryResult = {
  columns: string[]
  rows: unknown[][]
  truncated: boolean
}

type ColumnSchema = { name: string; dataType: string; isNullable: boolean; isPrimaryKey: boolean }
type TableSchema = { name: string; kind: string; columns?: ColumnSchema[]; foreignKeys?: { column: string; referencesTable: string; referencesColumn: string }[] }
type SchemaInfo = { tables?: TableSchema[] }

type Detected = {
  settingsKey: string
  name: string
  maskedConnectionString: string
  provider: string | null
  rejectionReason: string | null
}

const API = "/api/databases"

export default function DatabasesPage() {
  const [conns, setConns] = useState<DbConn[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // create form
  const [showCreate, setShowCreate] = useState(false)
  const [cName, setCName] = useState("")
  const [cProvider, setCProvider] = useState("Sqlite")
  const [cConnStr, setCConnStr] = useState("")
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
  const [activeTable, setActiveTable] = useState<{ connId: string; table: string } | null>(null)
  const treeItemRefs = useRef<Record<string, HTMLButtonElement | null>>({})
  const asideRef = useRef<HTMLElement | null>(null)
  const connSchemasRef = useRef<Record<string, SchemaInfo>>({})
  const schemaConnIdRef = useRef<string | null>(null)
  const schemaRetryRef = useRef<Record<string, number>>({})
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; connId: string; table?: string } | null>(null)
  const [schemaLoading, setSchemaLoading] = useState(false)
  const [browseTable, setBrowseTable] = useState<string | null>(null)
  const [browseResult, setBrowseResult] = useState<QueryResult | null>(null)
  const [browseLoading, setBrowseLoading] = useState(false)
  const [editing, setEditing] = useState<DbConn | null>(null)
  const [eName, setEName] = useState("")
  const [eProvider, setEProvider] = useState("Sqlite")
  const [eConnStr, setEConnStr] = useState("")
  const [eEnv, setEEnv] = useState("ReadOnly")
  const [eAllow, setEAllow] = useState(false)
  const [eRequire, setERequire] = useState(true)
  const [editTestResult, setEditTestResult] = useState<{ ok: boolean; message: string; latencyMs: number } | null>(null)
  const [editTesting, setEditTesting] = useState(false)
  const [testResults, setTestResults] = useState<Record<string, { ok: boolean; message: string; latencyMs: number } | undefined>>({})
  const [sql, setSql] = useState("SELECT 1")
  const [qResult, setQResult] = useState<QueryResult | null>(null)
  const [qError, setQError] = useState<string | null>(null)
  const [qLoading, setQLoading] = useState(false)

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
    setError(null)
    try {
      const res = await fetch(API, { cache: "no-store" })
      if (!res.ok) throw new Error(await res.text())
      const data = await res.json()
      setConns(data)
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "failed")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    const id = setTimeout(() => { void load() }, 0)
    return () => clearTimeout(id)
  }, [load])

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
      if (connId && table) {
        setActiveTable({ connId, table })
        setBrowseTable(table)
        void (async () => {
          // try cache first (localStorage) for instant render
          let schema: SchemaInfo | null = connSchemasRef.current[connId] ?? null
          if (!schema) {
            try {
              const raw = localStorage.getItem(schemaCacheKey(connId))
              if (raw) schema = JSON.parse(raw) as SchemaInfo
            } catch { schema = null }
          }
          if (schema) {
            setConnSchemas((prev) => (prev[connId] ? prev : { ...prev, [connId]: schema }))
            setSchemaInfo(schema)
          }
          // background revalidate / fetch
          void ensureSchema(connId)
          if (tab === "data") {
            void browseData(connId, table)
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
    const key = `${activeTable.connId}:${activeTable.table}`
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

  useEffect(() => { connSchemasRef.current = connSchemas }, [connSchemas])
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
      }))
      const params = new URLSearchParams()
      if (schemaConnId) params.set("connectionId", schemaConnId)
      if (activeTable?.table) params.set("table", activeTable.table)
      if (rightTab) params.set("tab", rightTab)
      const qs = params.toString()
      window.history.replaceState(null, "", qs ? "?" + qs : window.location.pathname)
    } catch {
      // ignore
    }
  }, [expandedConns, rightTab, selectedId, schemaConnId, activeTable])

  const create = async () => {
    try {
      const res = await fetch(API, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: cName,
          provider: cProvider,
          connectionString: cConnStr,
          environment: cEnv,
          allowAgentMutations: false,
          requireApprovalForMutations: true,
        }),
      })
      if (!res.ok) throw new Error(await res.text())
      setShowCreate(false)
      setCName(""); setCConnStr(""); setCreateTestResult(null)
      await load()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "create failed")
    }
  }

  const testConnectionCreate = async () => {
    setCreateTesting(true)
    setCreateTestResult(null)
    try {
      const res = await fetch(API + "/test-connection", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: cName || "test@local",
          provider: cProvider,
          connectionString: cConnStr,
          environment: cEnv,
          allowAgentMutations: false,
          requireApprovalForMutations: true,
        }),
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
  }

  const saveEdit = async () => {
    if (!editing) return
    try {
      const res = await fetch(API + "/" + editing.id, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: eName,
          environment: eEnv,
          connectionString: eConnStr,
          allowAgentMutations: eAllow,
          requireApprovalForMutations: eRequire,
        }),
      })
      if (!res.ok) throw new Error(await res.text())
      setEditing(null)
      await load()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "update failed")
    }
  }

  const testConnectionEdit = async () => {
    if (!editing) return
    setEditTesting(true)
    setEditTestResult(null)
    try {
      let res: Response
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
    try {
      const data = await fetchSchema(id)
      setSchemaInfo(data)
      setConnSchemas((prev) => ({ ...prev, [id]: data }))
      try { localStorage.setItem(schemaCacheKey(id), JSON.stringify(data)) } catch { /* quota */ }
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "schema failed")
    } finally {
      setSchemaLoading(false)
    }
  }

  const schemaCacheKey = (id: string) => `db.schema.cache.${id}`

  const fetchSchema = async (id: string) => {
    const res = await fetch(API + "/" + id + "/schema")
    if (!res.ok) throw new Error(await res.text())
    return await res.json()
  }

  // Stale-while-revalidate: render from localStorage cache instantly, then
  // refresh from backend in the background and update the cache.
  const ensureSchema = useCallback(async (id: string, opts?: { force?: boolean }) => {
    const force = opts?.force ?? false
    if (!force && connSchemasRef.current[id]) return // already in memory this session

    let cached: SchemaInfo | null = null
    if (!force) {
      try {
        const raw = localStorage.getItem(schemaCacheKey(id))
        if (raw) cached = JSON.parse(raw)
      } catch {
        cached = null
      }
    }

    if (cached) {
      // instant render from cache
      setConnSchemas((prev) => (prev[id] ? prev : { ...prev, [id]: cached }))
      if (schemaConnIdRef.current === id) setSchemaInfo(cached)
    }

    // background revalidate / initial fetch
    try {
      const fresh = await fetchSchema(id)
      setConnSchemas((prev) => ({ ...prev, [id]: fresh }))
      if (schemaConnIdRef.current === id) setSchemaInfo(fresh)
      try { localStorage.setItem(schemaCacheKey(id), JSON.stringify(fresh)) } catch { /* quota */ }
      schemaRetryRef.current[id] = 0 // reset retry counter on success
    } catch {
      // keep cached value on failure; auto-retry a few times
      const retries = schemaRetryRef.current[id] ?? 0
      if (retries < 3) {
        schemaRetryRef.current[id] = retries + 1
        setTimeout(() => { void ensureSchema(id) }, 2000 * (retries + 1))
      }
    }
  }, [])

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

  const selectTreeTable = async (connId: string, table: string) => {
    setActiveTable({ connId, table })
    setSelectedId(connId)
    setSchemaConnId(connId)
    setBrowseTable(table)
    // preserve current tab instead of forcing "data"
    // schema: use in-memory, then localStorage cache, then fetch
    let schema: SchemaInfo | null = connSchemasRef.current[connId] ?? null
    if (!schema) {
      try {
        const raw = localStorage.getItem(schemaCacheKey(connId))
        if (raw) schema = JSON.parse(raw) as SchemaInfo
      } catch { schema = null }
    }
    if (schema) {
      setConnSchemas((prev) => (prev[connId] ? prev : { ...prev, [connId]: schema }))
      setSchemaInfo(schema)
    }
    void ensureSchema(connId) // background revalidate / fetch
    // only fetch data if currently on Data tab
    if (rightTabRef.current === "data") {
      void browseData(connId, table)
    }
  }

  const browseData = async (id: string, table: string) => {
    setBrowseTable(table)
    setBrowseLoading(true)
    setBrowseResult(null)
    try {
      const conn = conns.find((c) => c.id === id)
      const isMySql = conn?.provider === "MySql"
      const quote = isMySql ? "`" : '"'
      const escaped = table.replace(new RegExp(quote, "g"), quote + quote)
      const sql = "SELECT * FROM " + quote + escaped + quote + " LIMIT 100"
      const res = await fetch(API + "/" + id + "/query", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql, maxRows: 100 }),
      })
      if (!res.ok) throw new Error(await res.text())
      setBrowseResult(await res.json())
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "browse failed")
    } finally {
      setBrowseLoading(false)
    }
  }

  const remove = async (id: string) => {
    await fetch(API + "/" + id, { method: "DELETE" })
    try { localStorage.removeItem(schemaCacheKey(id)) } catch { /* ignore */ }
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
    setQLoading(true); setQError(null); setQResult(null)
    try {
      let finalSql = sql.trim()
      if (!/\blimit\b/i.test(finalSql)) {
        finalSql = finalSql.replace(/;?\s*$/, "") + " LIMIT 100"
      }
      const res = await fetch(API + "/" + selectedId + "/query", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sql: finalSql, maxRows: 100 }),
      })
      if (!res.ok) throw new Error(await res.text())
      setQResult(await res.json())
    } catch (e: unknown) {
      setQError(e instanceof Error ? e.message : "query failed")
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
        <Button onClick={() => setShowCreate(!showCreate)}>
          <Plus className="mr-2 size-4" /> New connection
        </Button>

        <Button variant="outline" onClick={() => { void load(); for (const id of expandedConns) void ensureSchema(id, { force: true }) }}>
          <RefreshCw className="mr-2 size-4" /> Refresh
        </Button>
      </div>

      {error ? <p className="text-sm text-destructive">{error}</p> : null}

      {showCreate ? (
        <Card>
          <CardHeader><CardTitle>Create connection</CardTitle></CardHeader>
          <CardContent className="space-y-3">
            <Input placeholder="name (e.g. orders@host)" value={cName} onChange={e => setCName(e.target.value)} />
            <Select value={cProvider} onValueChange={(val) => { if (val) setCProvider(val) }}>
              <SelectTrigger className="w-full">
                <SelectValue placeholder="Provider" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Sqlite">Sqlite</SelectItem>
                <SelectItem value="MySql">MySql</SelectItem>
              </SelectContent>
            </Select>
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
            <div className="flex gap-2">
              <Button onClick={create}>Create</Button>
              <Button variant="outline" onClick={testConnectionCreate} disabled={createTesting || !cConnStr}>
                {createTesting ? "Testing…" : "Test"}
              </Button>
              <Button variant="outline" onClick={() => setShowCreate(false)}>Cancel</Button>
            </div>
            {createTestResult ? (
              <p className={`text-xs ${createTestResult.ok ? "text-green-600" : "text-destructive"}`}>
                {createTestResult.ok ? "OK" : "FAIL"} {createTestResult.message} ({createTestResult.latencyMs}ms)
              </p>
            ) : null}
          </CardContent>
        </Card>
      ) : null}

      {detected.length > 0 ? (
        <Card>
          <CardHeader><CardTitle>Detected connections</CardTitle><CardDescription>Passwords masked. Select keys to import (read-only).</CardDescription></CardHeader>
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
                  {(connSchemas[c.id]?.tables ?? []).map((t) => (
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
                  ))}

                </div>
              ) : null}
            </div>
          ))}
          {conns.length === 0 && !loading ? <p className="text-sm text-muted-foreground">No connections.</p> : null}
        </CardContent>
      </Card>

        </aside>
        <main className="flex-1 min-h-0 space-y-4 overflow-y-auto">
      <div className="flex gap-2 border-b pb-2">
        <Button size="sm" variant={rightTab === "data" ? "default" : "outline"} onClick={() => setRightTab("data")}>Data</Button>
        <Button size="sm" variant={rightTab === "schema" ? "default" : "outline"} onClick={() => setRightTab("schema")}>Schema</Button>
        <Button size="sm" variant={rightTab === "sql" ? "default" : "outline"} onClick={() => setRightTab("sql")}>SQL</Button>
      </div>
      {rightTab === "schema" && schemaConnId ? (
        <Card>
          <CardHeader>
            <CardTitle>Schema · {conns.find((c) => c.id === schemaConnId)?.name}</CardTitle>
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
        <Card>
          <CardHeader>
            <CardTitle>Data - {activeTable.table}</CardTitle>
          </CardHeader>
          <CardContent>
            {browseLoading ? <p className="text-sm text-muted-foreground">Loading.</p> : null}
            {browseResult ? (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr>{browseResult.columns.map((col: string) => <th key={col} className="border px-2 py-1 text-left">{col}</th>)}</tr>
                  </thead>
                  <tbody>
                    {browseResult.rows.map((row, i: number) => (
                      <tr key={i}>{row.map((v, j: number) => {
                        const text = String(v ?? "")
                        return <td key={j} className="border px-2 py-1 whitespace-nowrap" title={text}>{text.length > 60 ? text.slice(0, 60) + "…" : text}</td>
                      })}</tr>
                    ))}
                  </tbody>
                </table>
                {browseResult.truncated ? <p className="mt-1 text-xs text-muted-foreground">Truncated.</p> : null}
              </div>
            ) : null}
          </CardContent>
        </Card>
      ) : null}
      {rightTab === "sql" && selectedId ? (
        <Card>
          <CardHeader><CardTitle>Query - {conns.find(c => c.id === selectedId)?.name}</CardTitle></CardHeader>
          <CardContent className="space-y-3">
            <textarea className="w-full rounded border p-2 text-sm font-mono" value={sql} onChange={e => setSql(e.target.value)} rows={4} />
            <Button onClick={runQuery} disabled={qLoading}>
              <Play className="mr-2 size-4" /> {qLoading ? "Running…" : "Run"}
            </Button>
            {qError ? <p className="text-sm text-destructive">{qError}</p> : null}
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
                <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { selectTreeTable(ctxMenu.connId, ctxMenu.table!); setCtxMenu(null) }}>Open data</button>
                <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { const c = conns.find((x) => x.id === ctxMenu.connId); const q = c?.provider === "MySql" ? "`" : '"'; setSql(`SELECT * FROM ${q}${ctxMenu.table}${q}`); setSelectedId(ctxMenu.connId); setRightTab("sql"); setCtxMenu(null) }}>Query table</button>
              </>
            ) : null}
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { const c = conns.find((x) => x.id === ctxMenu.connId); if (c) openEdit(c); setCtxMenu(null) }}>Edit connection</button>
            <button className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => { loadSchema(ctxMenu.connId); setCtxMenu(null) }}>Refresh schema</button>
            <button className="block w-full rounded px-2 py-1 text-left text-sm text-destructive hover:bg-accent" onClick={() => { setDeletingId(ctxMenu.connId); setCtxMenu(null) }}>Delete connection</button>
          </div>
        </div>
      ) : null}

      {showPicker ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
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
                        if (pickerTarget === "edit") {
                          setEConnStr("Data Source=" + e.path)
                          setEName((prev) => {
                            if (prev.trim() !== "") return prev
                            const parts = e.path.split(/[\\\\/]/).filter(Boolean)
                            const file = parts.pop() ?? ""
                            const folder = parts.pop() ?? "local"
                            const base = file.replace(/\.[^.]+$/, "")
                            return `${base}@${folder}`
                          })
                        } else {
                          setCConnStr("Data Source=" + e.path)
                          setCName((prev) => {
                            if (prev.trim() !== "") return prev
                            const parts = e.path.split(/[\\\\/]/).filter(Boolean)
                            const file = parts.pop() ?? ""
                            const folder = parts.pop() ?? "local"
                            const base = file.replace(/\.[^.]+$/, "")
                            return `${base}@${folder}`
                          })
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
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
          <div className="w-full max-w-lg rounded-lg border border-border bg-background p-4 shadow-lg">
            <div className="mb-3 flex items-center justify-between">
              <p className="font-medium">Edit connection · {editing.name}</p>
              <Button variant="ghost" size="sm" onClick={() => setEditing(null)}>Close</Button>
            </div>
            <div className="space-y-3">
              <Input placeholder="name" value={eName} onChange={e => setEName(e.target.value)} />
              <Input value={eProvider} disabled />
              <div className="flex gap-2">
                <Input
                  placeholder="new connection string (leave empty to keep stored)"
                  value={eConnStr}
                  onChange={e => setEConnStr(e.target.value)}
                  className="flex-1"
                />
                <Button type="button" variant="outline" onClick={() => openPicker("edit")}>Browse</Button>
              </div>
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
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={eAllow} onChange={e => setEAllow(e.target.checked)} />
                Allow agent mutations
              </label>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={eRequire} onChange={e => setERequire(e.target.checked)} />
                Require approval for mutations
              </label>
              <div className="flex gap-2">
                <Button onClick={saveEdit}>Save</Button>
                <Button variant="outline" onClick={testConnectionEdit} disabled={editTesting}>
                  {editTesting ? "Testing…" : "Test"}
                </Button>
                <Button variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
              </div>
              {editTestResult ? (
                <p className={`text-xs ${editTestResult.ok ? "text-green-600" : "text-destructive"}`}>
                  {editTestResult.ok ? "OK" : "FAIL"} {editTestResult.message} ({editTestResult.latencyMs}ms)
                </p>
              ) : null}
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

       </div>
     </div>
  )
}

