"use client"

import { useCallback, useEffect, useMemo, useState } from "react"
import Link from "next/link"
import {
  FolderGit2,
  RefreshCw,
  Search,
  BookOpen,
  Radio,
  Clock,
  Layers,
  FileCode2,
  Trash2,
  AlertTriangle,
  Loader2,
  Cpu,
  MemoryStick,
} from "lucide-react"
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
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

type RuntimeDto = {
  processId: number
  headScopeRoot: string
  workspaceRoot: string
  codebaseEnabled: boolean
  dataDirectory: string
  startTime: string
  mcpEnabled: boolean
  mcpTransport: string
  lastHeartbeat: string
  isAlive: boolean
  memoryBytes?: number | null
  cpuPercent?: number | null
}

type CodebaseStatusDto = {
  files: number
  chunks: number
  lastIndexedAt: string | null
  dbPath?: string
}

type ProjectCodebaseStatusItem = {
  workspaceRoot: string
  files: number
  chunks: number
  lastIndexedAt: string | null
  dbPath: string | null
}

function projectName(root: string | undefined | null) {
  if (!root) return "—"
  const parts = root.split(/[/\\\\]+/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : root
}

function isRunning(r: RuntimeDto) {
  return r.isAlive && r.processId > 0
}

/** Working set in MB, one decimal. Bytes are what the OS reports. */
function formatMegabytes(bytes: number) {
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

type FilterStatus = "all" | "active" | "offline"

async function fetchProjectsAndStatuses() {
  const [runtimesRes, statusesRes] = await Promise.all([
    fetch("/api/coordinator/runtimes?includeRegistered=true", { cache: "no-store" }),
    fetch("/api/coordinator/projects/codebase-statuses", { cache: "no-store" }),
  ])
  if (!runtimesRes.ok) throw new Error(`HTTP ${runtimesRes.status} from coordinator`)
  const runtimes = (await runtimesRes.json()) as RuntimeDto[]

  const statuses: Record<string, CodebaseStatusDto> = {}
  if (statusesRes.ok) {
    const statusesList = (await statusesRes.json()) as ProjectCodebaseStatusItem[]
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

export function ProjectsList() {
  const [runtimes, setRuntimes] = useState<RuntimeDto[]>([])
  const [statusMap, setStatusMap] = useState<Record<string, CodebaseStatusDto>>({})
  const [loading, setLoading] = useState(true)
  const [reindexingRoot, setReindexingRoot] = useState<string | null>(null)
  const [query, setQuery] = useState("")
  const [filter, setFilter] = useState<FilterStatus>("all")
  const [error, setError] = useState<string | null>(null)

  // Delete modal state
  const [deleteTarget, setDeleteTarget] = useState<RuntimeDto | null>(null)
  const [deleteCodebaseIndex, setDeleteCodebaseIndex] = useState(true)
  const [deleteDotEling, setDeleteDotEling] = useState(false)
  const [isDeleting, setIsDeleting] = useState(false)

  const loadData = useCallback(async () => {
    try {
      const { runtimes: r, statuses: s } = await fetchProjectsAndStatuses()
      setRuntimes(r)
      setStatusMap(s)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load projects")
    }
  }, [])

  useEffect(() => {
    let isMounted = true
    void fetchProjectsAndStatuses()
      .then(({ runtimes: r, statuses: s }) => {
        if (!isMounted) return
        setRuntimes(r)
        setStatusMap(s)
      })
      .catch((err: unknown) => {
        if (!isMounted) return
        setError(err instanceof Error ? err.message : "Failed to load projects")
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
    const root = deleteTarget.workspaceRoot || deleteTarget.headScopeRoot
    setIsDeleting(true)
    setError(null)
    try {
      const res = await fetch("/api/coordinator/project/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          workspaceRoot: root,
          deleteCodebaseIndex,
          deleteDotEling,
        }),
      })
      if (!res.ok) throw new Error(`Delete failed (HTTP ${res.status})`)
      setDeleteTarget(null)
      await loadData()
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete project")
    } finally {
      setIsDeleting(false)
    }
  }

  const normalizedQuery = query.trim().toLowerCase()
  const filtered = useMemo(() => {
    return runtimes.filter((r) => {
      const root = r.workspaceRoot || r.headScopeRoot || ""
      const name = projectName(root).toLowerCase()
      const matchesQuery =
        !normalizedQuery ||
        name.includes(normalizedQuery) ||
        root.toLowerCase().includes(normalizedQuery) ||
        r.processId.toString().includes(normalizedQuery)

      if (!matchesQuery) return false

      if (filter === "active") return isRunning(r)
      if (filter === "offline") return !isRunning(r)
      return true
    })
  }, [runtimes, normalizedQuery, filter])

  const totalProjects = runtimes.length
  const activeCount = runtimes.filter(isRunning).length
  const totalFiles = Object.values(statusMap).reduce((acc, s) => acc + (s.files || 0), 0)

  return (
    <>
      <header className="flex h-16 shrink-0 items-center justify-between gap-2 border-b border-border/40 px-4">
        <div className="flex items-center gap-2">
          <SidebarTrigger className="-ml-1" />
          <Separator
            orientation="vertical"
            className="mr-2 data-vertical:h-4 data-vertical:self-auto"
          />
          <Breadcrumb>
            <BreadcrumbList>
              <BreadcrumbItem className="hidden md:block">
                <BreadcrumbLink href="/dashboard">Eling</BreadcrumbLink>
              </BreadcrumbItem>
              <BreadcrumbSeparator className="hidden md:block" />
              <BreadcrumbItem>
                <BreadcrumbPage>Projects</BreadcrumbPage>
              </BreadcrumbItem>
            </BreadcrumbList>
          </Breadcrumb>
        </div>

        <div className="flex items-center gap-2">
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
        </div>
      </header>

      <div className="flex min-w-0 min-h-0 flex-1 flex-col gap-4 overflow-hidden p-4 pt-4">
        {/* Metric tiles */}
        <div className="grid shrink-0 grid-cols-1 gap-3 sm:grid-cols-3">
          {loading ? (
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-20 rounded-lg" />
            ))
          ) : (
            <>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Registered Workspaces</div>
                <div className="text-xl font-medium">{totalProjects}</div>
              </div>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Active Instances</div>
                <div className="text-xl font-medium text-emerald-600 dark:text-emerald-400">
                  {activeCount}
                </div>
              </div>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Total Indexed Files</div>
                <div className="text-xl font-medium">{totalFiles}</div>
              </div>
            </>
          )}
        </div>

        {error && (
          <div className="shrink-0 rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {error}
          </div>
        )}

        {/* Filter & Search Bar */}
        <div className="flex shrink-0 flex-wrap items-center justify-between gap-3">
          <div className="relative flex-1 min-w-[240px] max-w-md">
            <Search className="absolute left-2.5 top-2.5 size-4 text-muted-foreground" />
            <Input
              placeholder="Search project name, path, or PID..."
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="pl-8"
            />
          </div>

          <div className="flex items-center gap-1">
            {(["all", "active", "offline"] as const).map((s) => (
              <button
                key={s}
                onClick={() => setFilter(s)}
                className={`rounded-md px-3 py-1.5 text-xs font-medium capitalize transition-colors ${
                  filter === s
                    ? "bg-primary text-primary-foreground"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                }`}
              >
                {s}
              </button>
            ))}
          </div>
        </div>

        {/* Projects List / Grid - Scrollable area */}
        <div className="min-w-0 min-h-0 flex-1 overflow-y-auto pr-1 flex flex-col gap-3">
          {loading ? (
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-28 w-full rounded-xl" />
            ))
          ) : filtered.length === 0 ? (
            <div className="flex min-h-[200px] flex-col items-center justify-center rounded-xl border border-dashed p-6 text-center text-sm text-muted-foreground">
              <FolderGit2 className="mb-2 size-8 opacity-40" />
              <span>No projects match the current filter.</span>
            </div>
          ) : (
            filtered.map((r) => {
              const root = r.workspaceRoot || r.headScopeRoot
              const name = projectName(root)
              const status = statusMap[root.toLowerCase()]
              const isAlive = isRunning(r)

              return (
                <div
                  key={root}
                  className="flex flex-col justify-between gap-3 rounded-xl border bg-card p-4 transition-colors hover:border-border sm:flex-row sm:items-center"
                >
                  <div className="flex flex-col gap-1.5 min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-semibold text-base">{name}</span>

                      {/* Status & PID Badge */}
                      {isAlive ? (
                        <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-500/10 px-2.5 py-0.5 text-xs font-medium text-emerald-600 dark:text-emerald-400">
                          <Radio className="size-3 animate-pulse" />
                          Running (PID: {r.processId})
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 rounded-full bg-muted px-2.5 py-0.5 text-xs font-medium text-muted-foreground">
                          Offline (PID: 0)
                        </span>
                      )}

                      {r.mcpEnabled && (
                        <span className="rounded-md border border-border/50 bg-muted/40 px-2 py-0.5 text-[11px] text-muted-foreground">
                          MCP ({r.mcpTransport})
                        </span>
                      )}
                    </div>

                    <div className="text-xs text-muted-foreground truncate" title={root}>
                      📁 {root}
                    </div>

                    {status?.dbPath && (
                      <div className="text-xs text-muted-foreground truncate" title={status.dbPath}>
                        🗄️ <span className="font-mono text-[11px]">{status.dbPath}</span>
                      </div>
                    )}

                    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground mt-1">
                      <span className="inline-flex items-center gap-1">
                        <Clock className="size-3.5" />
                        Last active: {utcToLocal(r.lastHeartbeat) || "—"}
                      </span>

                      {/* Only a live process has metrics to report. */}
                      {isAlive && r.memoryBytes != null && (
                        <span className="inline-flex items-center gap-1">
                          <Cpu className="size-3.5" />
                          CPU: {r.cpuPercent?.toFixed(1) ?? "0.0"}%
                        </span>
                      )}
                      {isAlive && r.memoryBytes != null && (
                        <span className="inline-flex items-center gap-1">
                          <MemoryStick className="size-3.5" />
                          RAM: {formatMegabytes(r.memoryBytes)}
                        </span>
                      )}

                      {status && (
                        <>
                          <span className="inline-flex items-center gap-1">
                            <FileCode2 className="size-3.5" />
                            {status.files} files ({status.chunks} chunks)
                          </span>
                          <span className="inline-flex items-center gap-1">
                            <Layers className="size-3.5" />
                            Indexed: {utcToLocal(status.lastIndexedAt) || "Never"}
                          </span>
                        </>
                      )}
                    </div>
                  </div>

                  <div className="flex flex-col items-stretch gap-2 self-stretch shrink-0 sm:w-40 sm:self-center">
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => void triggerReindex(root)}
                      disabled={reindexingRoot === root}
                      title="Trigger incremental re-indexing for this project"
                      className="justify-start"
                    >
                      <RefreshCw
                        className={`size-3.5 mr-1 ${
                          reindexingRoot === root ? "animate-spin" : ""
                        }`}
                      />
                      Re-index
                    </Button>

                    <Button variant="outline" size="sm" className="justify-start" render={<Link href="/dashboard/codebase" />}>
                      <BookOpen className="size-3.5 mr-1" />
                      View Codebase
                    </Button>

                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => {
                        setDeleteTarget(r)
                        setDeleteCodebaseIndex(true)
                        setDeleteDotEling(false)
                      }}
                      className="justify-start border-destructive/30 text-destructive hover:bg-destructive/10 hover:text-destructive"
                      title="Delete project"
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
              <AlertDialogTitle>Delete Project Confirmation</AlertDialogTitle>
            </div>
            <AlertDialogDescription>
              Select the components you want to remove for this project:
            </AlertDialogDescription>
          </AlertDialogHeader>

          {deleteTarget && (
            <div className="flex flex-col gap-3 py-2 text-xs">
              <div className="rounded-lg border bg-muted/40 p-3">
                <div className="font-semibold text-foreground text-sm">
                  {projectName(deleteTarget.workspaceRoot || deleteTarget.headScopeRoot)}
                </div>
                <div className="text-muted-foreground break-all mt-0.5">
                  {deleteTarget.workspaceRoot || deleteTarget.headScopeRoot}
                </div>
                {deleteTarget.isAlive && deleteTarget.processId > 0 && (
                  <div className="mt-2 text-amber-600 dark:text-amber-400 font-medium">
                    ⚠️ Instance is currently running (PID: {deleteTarget.processId}). Deleting will unregister this instance from coordinator registry.
                  </div>
                )}
              </div>

              <div className="space-y-2 pt-1">
                <div className="font-medium text-foreground text-xs">Components to remove:</div>

                {/* Always deleted: Registry */}
                <label className="flex items-start gap-2.5 rounded-lg border bg-card p-3 opacity-90 cursor-not-allowed">
                  <input
                    type="checkbox"
                    checked={true}
                    disabled={true}
                    className="mt-0.5 accent-primary"
                  />
                  <div className="flex-1">
                    <div className="font-medium text-foreground">
                      Workspace Registry Record (Required)
                    </div>
                    <div className="text-muted-foreground text-[11px] mt-0.5">
                      Removes the workspace row from the global projects.db database and unregisters the active instance if running.
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
                    className="mt-0.5 accent-destructive"
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
              disabled={isDeleting}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              {isDeleting ? (
                <>
                  <Loader2 className="size-4 animate-spin mr-1.5" />
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
