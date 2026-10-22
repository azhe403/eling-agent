"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import Link from "next/link"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import {
  AlertTriangle,
  BookOpen,
  ChevronDown,
  Clock,
  Cpu,
  FileCode2,
  FolderGit2,
  Layers,
  Loader2,
  MemoryStick,
  Radio,
  RefreshCw,
  Trash2,
} from "lucide-react"
import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
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
import { useMemoriesSse, type SseTopic } from "@/hooks/use-memories-sse"
import { utcToLocal } from "@/lib/date-utils"
import { formatMegabytes } from "@/lib/format"
import { PathText } from "@/components/path-text"
import { group, uptime, type RuntimeDto, type WorkspaceGroup } from "./aggregate"

type CodebaseStatusDto = {
  files: number
  chunks: number
  lastIndexedAt: string | null
  dbPath?: string
}

type WorkspaceCodebaseStatusItem = {
  workspaceRoot: string
  files: number
  chunks: number
  lastIndexedAt: string | null
  dbPath: string | null
}

function workspaceName(root: string | undefined | null) {
  if (!root) return "—"
  const parts = root.split(/[/\\]+/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : root
}

type FilterStatus = "all" | "active" | "offline"

async function fetchWorkspacesAndStatuses() {
  // allProcesses: every live eling process, not the old single deduped row per
  // workspace — one folder can host several sessions at once. Grouping and the
  // metric sums happen client-side in ./aggregate.
  const [runtimesRes, statusesRes] = await Promise.all([
    fetch("/api/coordinator/runtimes?includeRegistered=true&allProcesses=true", { cache: "no-store" }),
    fetch("/api/coordinator/projects/codebase-statuses", { cache: "no-store" }),
  ])
  if (!runtimesRes.ok) throw new Error(`HTTP ${runtimesRes.status} from coordinator`)
  const runtimes = (await runtimesRes.json()) as RuntimeDto[]

  const statuses: Record<string, CodebaseStatusDto> = {}
  if (statusesRes.ok) {
    const statusesList = (await statusesRes.json()) as WorkspaceCodebaseStatusItem[]
    for (const item of statusesList) {
      statuses[item.workspaceRoot.toLowerCase()] = {
        files: item.files,
        chunks: item.chunks,
        lastIndexedAt: item.lastIndexedAt,
        dbPath: item.dbPath || undefined,
      }
    }
  }
  return { runtimes, statuses }
}

export function WorkspacesList() {
  const [groups, setGroups] = useState<WorkspaceGroup[]>([])
  const [statusMap, setStatusMap] = useState<Record<string, CodebaseStatusDto>>({})
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [loading, setLoading] = useState(true)
  const [reindexingRoot, setReindexingRoot] = useState<string | null>(null)
  // Filter and search live in the URL (?status=active&q=...), so a reload, a
  // shared link, and the back button all land on the same view. Absent params
  // mean the default view, which keeps shared URLs clean.
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()

  const statusParam = searchParams.get("status")
  const filter: FilterStatus =
    statusParam === "active" || statusParam === "offline" ? statusParam : "all"
  const query = searchParams.get("q") ?? ""

  const setParam = useCallback(
    (key: "status" | "q", value: string | null) => {
      const params = new URLSearchParams(searchParams.toString())
      if (value === null || value === "") params.delete(key)
      else params.set(key, value)
      const qs = params.toString()
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [router, pathname, searchParams]
  )

  const setQuery = useCallback((q: string) => setParam("q", q), [setParam])
  const setFilter = useCallback(
    (f: FilterStatus) => setParam("status", f === "all" ? null : f),
    [setParam]
  )
  const [error, setError] = useState<string | null>(null)

  // Delete modal state
  const [deleteTarget, setDeleteTarget] = useState<WorkspaceGroup | null>(null)
  const [deleteRegistry, setDeleteRegistry] = useState(true)
  const [deleteCodebaseIndex, setDeleteCodebaseIndex] = useState(true)
  const [deleteDotEling, setDeleteDotEling] = useState(false)
  const [isDeleting, setIsDeleting] = useState(false)

  const loadData = useCallback(async () => {
    try {
      const { runtimes, statuses } = await fetchWorkspacesAndStatuses()
      setGroups(group(runtimes))
      setStatusMap(statuses)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load workspaces")
    }
  }, [])

  useEffect(() => {
    let isMounted = true
    void fetchWorkspacesAndStatuses()
      .then(({ runtimes, statuses }) => {
        if (!isMounted) return
        setGroups(group(runtimes))
        setStatusMap(statuses)
      })
      .catch((err: unknown) => {
        if (!isMounted) return
        setError(err instanceof Error ? err.message : "Failed to load workspaces")
      })
      .finally(() => {
        if (isMounted) setLoading(false)
      })
    return () => {
      isMounted = false
    }
  }, [])

  // Auto-refresh every 10s so CPU/RAM stay live. The interval only exists while
  // this page is mounted, and it is cleared on unmount, so navigating away stops
  // the polling entirely. `loadData` is silent: no spinner, no error flash, so a
  // background tick never disturbs what the user is reading or clicking.
  useEffect(() => {
    const POLL_MS = 10_000
    const id = setInterval(() => {
      void loadData()
    }, POLL_MS)
    return () => clearInterval(id)
  }, [loadData])

  // Live SSE update when runtimes register/unregister or codebase re-indexes
  const handleReload = useCallback(() => {
    void loadData()
  }, [loadData])

  const sseTopics = useMemo<SseTopic[]>(() => ["runtimes", "codebase"], [])
  const { status: sseStatus } = useMemoriesSse(handleReload, handleReload, sseTopics)

  const toggleExpanded = useCallback((root: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(root)) next.delete(root)
      else next.add(root)
      return next
    })
  }, [])

  const triggerReindex = async (root: string) => {
    setReindexingRoot(root)
    try {
      const res = await fetch(
        `/api/codebase/rebuild-index?full=false&project=${encodeURIComponent(root)}`,
        { method: "POST" }
      )
      if (!res.ok) throw new Error(`Re-index failed (HTTP ${res.status})`)
      void loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to trigger re-index")
    } finally {
      setReindexingRoot(null)
    }
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    setIsDeleting(true)
    setError(null)
    try {
      const res = await fetch("/api/coordinator/project/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          workspaceRoot: deleteTarget.root,
          deleteRegistry,
          deleteCodebaseIndex,
          deleteDotEling,
        }),
      })
      if (!res.ok) throw new Error(`Delete failed (HTTP ${res.status})`)
      setDeleteTarget(null)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete workspace")
    } finally {
      setIsDeleting(false)
    }
  }

  const normalizedQuery = query.trim().toLowerCase()
  const filtered = useMemo(() => {
    return groups.filter((g) => {
      const name = workspaceName(g.root).toLowerCase()
      const matchesQuery =
        !normalizedQuery ||
        name.includes(normalizedQuery) ||
        g.root.toLowerCase().includes(normalizedQuery) ||
        g.processes.some((p) => p.pid.toString().includes(normalizedQuery))

      if (!matchesQuery) return false

      if (filter === "active") return g.running
      if (filter === "offline") return !g.running
      return true
    })
  }, [groups, normalizedQuery, filter])

  const activeWorkspaces = groups.filter((g) => g.running).length
  // Processes, not rows: several sessions can share one workspace, and that
  // count is the honest number for a tile labelled "instances".
  const totalInstances = groups.reduce((sum, g) => sum + g.processes.length, 0)
  const totalFiles = Object.values(statusMap).reduce((acc, s) => acc + (s.files || 0), 0)

  return (
    <>
      <DashboardHeader label="Workspaces">
          <div
            className="flex items-center gap-1.5 rounded-full border border-border/50 bg-muted/30 px-2.5 py-1 text-[11px] font-medium"
            title={
              sseStatus === "connected"
                ? "Live sync active"
                : "Live sync connecting..."
            }
          >
            <span
              className={`inline-block size-2 rounded-full ${
                sseStatus === "connected"
                  ? "bg-emerald-500 animate-pulse"
                  : sseStatus === "connecting"
                    ? "bg-amber-500"
                    : "bg-destructive"
              }`}
            />
            <span className="text-muted-foreground">
              {sseStatus === "connected"
                ? "Live"
                : sseStatus === "connecting"
                  ? "Connecting"
                  : "Offline"}
            </span>
          </div>

          <Button
            variant="outline"
            size="sm"
            onClick={async () => {
              setLoading(true)
              await loadData()
              setLoading(false)
            }}
            disabled={loading}
          >
            <RefreshCw className={`size-3.5 ${loading ? "animate-spin" : ""}`} />
            <span className="hidden sm:inline-block">Refresh</span>
          </Button>
      </DashboardHeader>

      <div className="flex min-w-0 flex-1 flex-col gap-4 p-4 pt-4">
        {/* Metric tiles */}
        <div className="grid shrink-0 grid-cols-1 gap-3 sm:grid-cols-3">
          {loading ? (
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-20 rounded-lg" />
            ))
          ) : (
            <>
              {/*
                Each tile is one announcement, not three loose text nodes: the
                label, the number and the qualifier are grouped and named as a
                sentence, and the visible children are hidden from assistive
                tech so the same words are not read twice. The visible layout
                is untouched.
              */}
              <div
                className="rounded-lg border p-3 text-sm"
                role="group"
                aria-label={`Registered workspaces: ${groups.length}`}
              >
                <div className="text-muted-foreground" aria-hidden="true">
                  Registered Workspaces
                </div>
                <div className="text-xl font-medium" aria-hidden="true">
                  {groups.length}
                </div>
              </div>
              <div
                className="rounded-lg border p-3 text-sm"
                role="group"
                aria-label={`Active instances: ${totalInstances} across ${activeWorkspaces} active workspaces`}
              >
                <div className="text-muted-foreground" aria-hidden="true">
                  Active Instances
                </div>
                <div
                  className="text-xl font-medium text-emerald-600 dark:text-emerald-400"
                  aria-hidden="true"
                >
                  {totalInstances}
                </div>
                <div
                  className="text-[11px] text-muted-foreground"
                  aria-hidden="true"
                >
                  across {activeWorkspaces} active workspaces
                </div>
              </div>
              <div
                className="rounded-lg border p-3 text-sm"
                role="group"
                aria-label={`Total indexed files: ${totalFiles.toLocaleString()}`}
              >
                <div className="text-muted-foreground" aria-hidden="true">
                  Total Indexed Files
                </div>
                <div className="text-xl font-medium" aria-hidden="true">
                  {totalFiles}
                </div>
              </div>
            </>
          )}
        </div>

        {error && (
          <div className="shrink-0 rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {error}
          </div>
        )}

        {/* Filter & Search Bar — same two-row toolbar pattern as the memories page */}
        <div className="sticky top-16 z-10 flex flex-col gap-2 bg-background pb-2 pt-1">
          <Input
            placeholder="Search workspace name, path, or PID..."
            aria-label="Search workspaces"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            className="min-w-0 flex-1"
          />

          <div className="flex flex-wrap items-center gap-2">
            <div
              className="flex shrink-0 items-center gap-1 rounded-lg border bg-muted/40 p-1"
              role="group"
              aria-label="Workspace status"
            >
              {(["all", "active", "offline"] as const).map((s) => (
                <button
                  key={s}
                  onClick={() => setFilter(s)}
                  className={
                    (filter === s
                      ? "rounded-md bg-background px-2.5 py-1 text-xs font-medium text-foreground shadow-sm"
                      : "rounded-md px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground") +
                    " whitespace-nowrap"
                  }
                >
                  {s === "all" ? "All" : s === "active" ? "Active" : "Offline"}
                </button>
              ))}
            </div>
          </div>
        </div>

        {/* Workspaces List / Grid — 2 columns on wide screens (2xl, ≥1536px) */}
        <div className="grid min-w-0 grid-cols-1 gap-3 2xl:grid-cols-2">
          {loading ? (
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-28 w-full rounded-xl" />
            ))
          ) : filtered.length === 0 ? (
            <div className="flex min-h-[200px] flex-col items-center justify-center rounded-xl border border-dashed p-6 text-center text-sm text-muted-foreground 2xl:col-span-2">
              <FolderGit2 className="mb-2 size-8 opacity-40" />
              <span>No workspaces match the current filter.</span>
            </div>
          ) : (
            filtered.map((g) => {
              const name = workspaceName(g.root)
              const status = statusMap[g.root.toLowerCase()]
              const key = g.root.toLowerCase()
              const single = g.processes.length === 1
              const expandedFlag = expanded.has(key)

              return (
                <div
                  key={key}
                  className="flex flex-col justify-between gap-3 rounded-xl border bg-card p-4 transition-colors hover:border-border sm:flex-row sm:items-center"
                >
                  <div className="flex flex-col gap-1.5 min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-semibold text-base">{name}</span>

                      {/* Status & PID Badge */}
                      {g.running ? (
                        <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-500/10 px-2.5 py-0.5 text-xs font-medium text-emerald-600 dark:text-emerald-400">
                          <Radio className="size-3 animate-pulse" />
                          {single ? `Running (PID: ${g.processes[0].pid})` : `Running · ${g.processes.length} instances`}
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 rounded-full bg-muted px-2.5 py-0.5 text-xs font-medium text-muted-foreground">
                          Offline
                        </span>
                      )}

                      {g.transport && (
                        <span className="rounded-md border border-border/50 bg-muted/40 px-2 py-0.5 text-[11px] text-muted-foreground">
                          MCP ({g.transport})
                        </span>
                      )}

                      {g.ownerPort && (
                        <span
                          className="inline-flex items-center gap-1 rounded-full bg-blue-500/10 px-2.5 py-0.5 text-xs font-medium text-blue-600 dark:text-blue-400"
                          title={`Memegang port dashboard :${g.ownerPort}`}
                        >
                          owner · :{g.ownerPort}
                        </span>
                      )}

                      {/* Per-process drill-down only pays off past one instance. */}
                      {g.running && !single && (
                        <button
                          type="button"
                          onClick={() => toggleExpanded(key)}
                          aria-expanded={expandedFlag}
                          aria-label={`Toggle process list for ${name}`}
                          title={expandedFlag ? "Hide processes" : "Show processes"}
                          className="inline-flex size-6 items-center justify-center rounded-md border border-border/50 text-muted-foreground transition-colors hover:bg-muted"
                        >
                          <ChevronDown
                            className={`size-3.5 transition-transform ${expandedFlag ? "rotate-180" : ""}`}
                          />
                        </button>
                      )}
                    </div>

                    <PathText prefix="📁" path={g.root} className="text-xs" />

                    {/* The db file path only means something once an index exists —
                        hiding it for never-indexed workspaces keeps the card scannable. */}
                    {status?.dbPath && status.lastIndexedAt && (
                      <PathText
                        prefix="🗄️"
                        path={status.dbPath}
                        className="font-mono text-[11px]"
                      />
                    )}

                    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground mt-1">
                      <span className="inline-flex items-center gap-1">
                        <Clock className="size-3.5" />
                        Last active: {utcToLocal(g.lastHeartbeat) || "—"}
                      </span>

                      {/* Sums across every live process of this workspace; null
                          until at least one process has been sampled. */}
                      {g.running && g.totalMemoryBytes != null && (
                        <span
                          className="inline-flex items-center gap-1"
                          title="Akumulasi semua proses workspace ini (100% = seluruh core)"
                        >
                          <Cpu className="size-3.5" />
                          CPU: {(g.totalCpuPercent ?? 0).toFixed(1)}%
                        </span>
                      )}
                      {g.running && g.totalMemoryBytes != null && (
                        <span className="inline-flex items-center gap-1">
                          <MemoryStick className="size-3.5" />
                          RAM: {formatMegabytes(g.totalMemoryBytes)}
                        </span>
                      )}

                      {status && (
                        <>
                          {/* A zero count is noise until an index exists — the
                              "Indexed: Never" chip below already says it all. */}
                          {status.lastIndexedAt && (
                            <span className="inline-flex items-center gap-1">
                              <FileCode2 className="size-3.5" />
                              {status.files} files ({status.chunks} chunks)
                            </span>
                          )}
                          <span className="inline-flex items-center gap-1">
                            <Layers className="size-3.5" />
                            Indexed: {utcToLocal(status.lastIndexedAt) || "Never"}
                          </span>
                        </>
                      )}
                    </div>

                    {expandedFlag && g.processes.length > 1 && (
                      <div className="mt-2 flex flex-col gap-1 border-t border-border/50 pt-2">
                        {g.processes.map((p) => (
                          <div
                            key={p.pid}
                            className="flex items-center gap-2 rounded-lg bg-muted/40 px-2.5 py-1.5 text-xs"
                          >
                            <span
                              className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${
                                p.owner
                                  ? "bg-blue-500/10 text-blue-600 dark:text-blue-400"
                                  : "bg-muted text-muted-foreground"
                              }`}
                              title={
                                p.owner
                                  ? "Owns the dashboard HTTP host (port 4417)"
                                  : "MCP-only session attached to the workspace"
                              }
                            >
                              {p.owner ? "owner" : "peer"}
                            </span>
                            <span className="font-mono font-medium">{p.pid}</span>
                            <span className="text-muted-foreground">
                              {uptime(p.startTime)} · {p.transport}
                            </span>
                            {p.memoryBytes != null && (
                              <span className="ml-auto font-mono text-muted-foreground">
                                {formatMegabytes(p.memoryBytes)} · {(p.cpuPercent ?? 0).toFixed(1)}%
                              </span>
                            )}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>

                  <div className="flex flex-col items-stretch gap-2 self-stretch shrink-0 sm:w-40 sm:self-center">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => void triggerReindex(g.root)}
                      disabled={reindexingRoot === g.root}
                      title="Trigger incremental re-indexing for this workspace"
                      className="justify-start"
                    >
                      <RefreshCw
                        className={`size-3.5 mr-1 ${
                          reindexingRoot === g.root ? "animate-spin" : ""
                        }`}
                      />
                      Re-index
                    </Button>

                    <Button
                      variant="outline"
                      size="sm"
                      className="justify-start"
                      nativeButton={false}
                      render={<Link href="/dashboard/codebase" />}
                    >
                      <BookOpen className="size-3.5 mr-1" />
                      View Codebase
                    </Button>

                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => {
                        setDeleteTarget(g)
                        setDeleteRegistry(true)
                        setDeleteCodebaseIndex(true)
                        setDeleteDotEling(false)
                      }}
                      className="justify-start border-destructive/30 text-destructive hover:bg-destructive/10 hover:text-destructive"
                      title="Delete workspace"
                    >
                      <Trash2 className="size-3.5 mr-1" />
                      Delete
                    </Button>
                  </div>
                </div>
              )
            })
          )}
        </div>
      </div>

      {/* Confirmation & Scope Selection Dialog */}
      <AlertDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setDeleteTarget(null)
          }
        }}
      >
        <AlertDialogContent
          className="w-[94vw] sm:max-w-xl"
          onOverlayClick={() => setDeleteTarget(null)}
        >
          <AlertDialogHeader>
            <div className="flex items-center gap-2 text-destructive">
              <AlertTriangle className="size-5" />
              <AlertDialogTitle>Delete Workspace Confirmation</AlertDialogTitle>
            </div>
            <AlertDialogDescription>
              Select the components to remove for this workspace:
            </AlertDialogDescription>
          </AlertDialogHeader>

          {deleteTarget && (
            <div className="flex flex-col gap-3 py-2 text-xs">
              <div className="rounded-lg border bg-muted/40 p-3">
                <div className="font-semibold text-foreground text-sm">
                  {workspaceName(deleteTarget.root)}
                </div>
                <div className="text-muted-foreground break-all mt-0.5">
                  {deleteTarget.root}
                </div>
                {deleteTarget.processes.length === 1 && (
                  <div className="mt-2 text-amber-600 dark:text-amber-400 font-medium">
                    ⚠️ Instance is currently running (PID: {deleteTarget.processes[0].pid}). Deleting will unregister this instance from coordinator registry.
                  </div>
                )}
                {deleteTarget.processes.length > 1 && (
                  <div className="mt-2 text-amber-600 dark:text-amber-400 font-medium">
                    ⚠️ {deleteTarget.processes.length} instances are running (PIDs:{" "}
                    {deleteTarget.processes.map((p) => p.pid).join(", ")}). Deleting will
                    unregister them from the coordinator registry — sessions that are still
                    alive will re-register on their next heartbeat.
                  </div>
                )}
              </div>

              <div className="space-y-2 pt-1">
                <div className="font-medium text-foreground text-xs">Components to remove:</div>

                {/* Always deleted before: now one optable component, because
                    clearing only the index cache is a legitimate request. */}
                <label className="flex items-start gap-2.5 rounded-lg border bg-card p-3 cursor-pointer hover:bg-accent/40 transition-colors">
                  <input
                    type="checkbox"
                    checked={deleteRegistry}
                    onChange={(e) => setDeleteRegistry(e.target.checked)}
                    className="mt-0.5 accent-primary"
                  />
                  <div className="flex-1">
                    <div className="font-medium text-foreground">
                      Workspace Registry Record
                    </div>
                    <div className="text-muted-foreground text-[11px] mt-0.5">
                      Removes the workspace row from the global workspace registry (agent-workspaces.json) and unregisters the active instance if running. Leave unchecked to keep the workspace listed.
                    </div>
                  </div>
                </label>

                {/* Option: Codebase Index SQLite DB */}
                <label className="flex items-start gap-2.5 rounded-lg border bg-card p-3 cursor-pointer hover:bg-accent/40 transition-colors">
                  <input
                    type="checkbox"
                    checked={deleteCodebaseIndex}
                    onChange={(e) => setDeleteCodebaseIndex(e.target.checked)}
                    className="mt-0.5 accent-primary"
                  />
                  <div className="flex-1">
                    <div className="font-medium text-foreground">
                      Codebase FTS Index Cache (SQLite)
                    </div>
                    <div className="text-muted-foreground text-[11px] mt-0.5">
                      Deletes the SQLite codebase index file from global storage (~/.local/share/eling/codebase/...).
                    </div>
                  </div>
                </label>

                {/* Option: Local .eling folder */}
                <label className="flex items-start gap-2.5 rounded-lg border bg-card p-3 cursor-pointer hover:bg-destructive/10 transition-colors">
                  <input
                    type="checkbox"
                    checked={deleteDotEling}
                    onChange={(e) => setDeleteDotEling(e.target.checked)}
                    className="mt-0.5 accent-primary"
                  />
                  <div className="flex-1">
                    <div className="font-medium text-destructive">
                      Local .eling Directory & Memories
                    </div>
                    <div className="text-muted-foreground text-[11px] mt-0.5">
                      ⚠️ Permanently deletes the local .eling folder and stored markdown memory files inside the workspace directory. Other source code remains untouched.
                    </div>
                  </div>
                </label>
              </div>
            </div>
          )}

          <AlertDialogFooter>
            <AlertDialogCancel disabled={isDeleting}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={(e) => {
                e.preventDefault()
                void handleDelete()
              }}
              disabled={
                isDeleting ||
                (!deleteRegistry && !deleteCodebaseIndex && !deleteDotEling)
              }
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {isDeleting ? (
                <>
                  <Loader2 className="size-4 mr-1.5" />
                  Deleting...
                </>
              ) : (
                "Confirm Delete"
              )}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}
