"use client"

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react"
import { useRouter, useSearchParams } from "next/navigation"
import { Plus, RefreshCw } from "lucide-react"

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
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
import { Skeleton } from "@/components/ui/skeleton"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import { utcToLocal } from "@/lib/date-utils"
import { TYPES } from "@/lib/types"
import type { Memory } from "@/lib/types"

import { MemoryCard } from "./MemoryCard"
import { MemoryEditor } from "./MemoryEditor"

function projectName(root: string | undefined | null) {
  if (!root) return "—"
  const parts = root.split(/[/\\]+/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : root
}

function scopeLabel(value: string | undefined | null) {
  if (!value) return "Select scope..."
  if (value === "all") return "🌐 All projects"
  if (value === "global") return "🌐 Global Scope"
  return "📁 " + projectName(value)
}

type ProjectScope = {
  root: string
  name: string
  writable: boolean
  hasLiveRuntime: boolean
}

/**
 * Memory scopes come from /api/project/scopes, not from the coordinator's
 * runtime list. A scope is a `.eling` directory; a runtime row means a process
 * is alive somewhere. They differ exactly where it matters — a scope nobody is
 * running in is still readable, and reading runtimes hid it.
 */
async function loadProjectScopes(): Promise<ProjectScope[]> {
  const res = await fetch("/api/project/scopes", {
    cache: "no-store",
    headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
  })
  if (!res.ok) return []
  const data = await res.json()
  return Array.isArray(data?.scopes) ? (data.scopes as ProjectScope[]) : []
}

export function MemoriesList() {
  const router = useRouter()

  // Server + initial client renders always assume desktop/loaded; the real value
  // is applied right after hydration, avoiding a server/client mismatch.
  const mounted = useSyncExternalStore(
    () => () => {},
    () => true,
    () => false
  )

  const [memories, setMemories] = useState<Memory[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [type, setType] = useState("All")
  const [query, setQuery] = useState("")
  const [editingId, setEditingId] = useState<string | null>(null)
  const editingIdRef = useRef<string | null>(null)
  useEffect(() => {
    editingIdRef.current = editingId
  }, [editingId])

  const searchParams = useSearchParams()
  const scope = searchParams.get("scope") ?? "all"
  const [copiedId, setCopiedId] = useState<string | null>(null)

  const handleScopeChange = useCallback((value: string | null) => {
    if (!value) return
    if (value === "all") router.push("/dashboard/memories")
    else router.push("/dashboard/memories?scope=" + encodeURIComponent(value))
  }, [router])
  const [promoteTarget, setPromoteTarget] = useState<Memory | null>(null)
  const [promoteAsMove, setPromoteAsMove] = useState(false)
  const [copyTarget, setCopyTarget] = useState<Memory | null>(null)
  const [copyAsMove, setCopyAsMove] = useState(false)
  const [copyProjectRoot, setCopyProjectRoot] = useState<string>("")
  const [deleteTarget, setDeleteTarget] = useState<Memory | null>(null)
  const [scopes, setScopes] = useState<ProjectScope[]>([])
  // Copy targets are the scopes this workspace may actually write to.
  const copyTargets = scopes.filter((s) => s.writable)

  const copyToClipboard = useCallback((text: string, id: string) => {
    navigator.clipboard.writeText(text).catch(() => {
      // Clipboard API can be blocked (permissions / insecure context);
      // ignore so the failure never surfaces as an unhandled rejection.
    })
    setCopiedId(id)
    setTimeout(() => setCopiedId(null), 1500)
  }, [])

  const scrollToTop = useCallback(() => {
    if (typeof window !== "undefined") {
      window.scrollTo({ top: 0, behavior: "smooth" })
    }
  }, [])

  const loadScopes = useCallback(async () => {
    try {
      setScopes(await loadProjectScopes())
    } catch {
      // ignore — the picker still offers All and Global
    }
  }, [])

  const load = useCallback(
    async (triggeredBy?: string) => {
      try {
        const t = Date.now()
        let url = `/api/aggregated/memories?limit=100&_t=${t}`
        if (scope === "global")
          url = `/api/global/memories?limit=100&_t=${t}`
        else if (scope !== "all")
          url = `/api/project/memories?projectRoot=${encodeURIComponent(scope)}&limit=100&_t=${t}`

        console.log(
          `%c[FETCH MEMORIES] 📡 Fetching fresh data${triggeredBy ? ` (Trigger: ${triggeredBy})` : ""} from ${url}`,
          "color: #0284c7; font-weight: bold;"
        )

        const res = await fetch(url, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
        })
        if (!res.ok) throw new Error(`API returned ${res.status}`)
        const data = await res.json()

        // Trust the backend: it is the single source of truth for memory data,
        // already deduped by MemoryMerger and RuntimeRegistry.
        const normalized: Memory[] = Array.isArray(data)
          ? data.map((m: Memory) => ({
              ...m,
              scope:
                (m.scope as string) ??
                (scope === "global"
                  ? "global"
                  : scope === "all"
                    ? (m.scope ?? "project")
                    : "project"),
              project:
                m.project ??
                (scope !== "all" && scope !== "global"
                  ? {
                      id: scope.split("\\").pop() ?? scope,
                      root: scope,
                    }
                  : null),
            }))
          : []

        setMemories(normalized)
        console.log(
          `%c[MEMORIES LOADED] ✅ ${normalized.length} memories loaded at ${new Date().toLocaleTimeString()}`,
          "color: #16a34a; font-weight: bold;"
        )
      } catch (e) {
        console.error("[FETCH ERROR]", e)
        setError(e instanceof Error ? e.message : "Failed to load memories")
      } finally {
        setLoading(false)
      }
    },
    [scope]
  )

  // Fetch scopes and memories on mount / scope change
  useEffect(() => {
    let isMounted = true

    const fetchAll = async () => {
      await loadScopes()
      if (isMounted) {
        await load("mount_or_scope_change")
      }
    }

    void fetchAll()

    return () => {
      isMounted = false
    }
  }, [load, loadScopes])

  // Real-time memory and scope refresh via Server-Sent Events (SSE).
  const { status: sseStatus } = useMemoriesSse(
    useCallback(() => {
      void load("sse_mutation")
    }, [load]),
    useCallback(() => {
      void loadScopes()
      void load("sse_runtimes")
    }, [load, loadScopes])
  )

  async function remove(m: Memory) {
    let url = `/api/memories/${m.id}`
    if (m.scope === "global") url = `/api/global/memories/${m.id}`
    else if (m.scope === "project" && m.project?.root)
      url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(m.project.root)}`
    else if (scope !== "all" && scope !== "global")
      url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(scope)}`
    const res = await fetch(url, { method: "DELETE" })
    if (res.ok || res.status === 404) {
      setMemories((x) => x.filter((y) => y.id !== m.id))
    }
  }

  async function save(
    id: string,
    body: { content: string; type: string; status: string }
  ) {
    const target = memories.find((x) => x.id === id)
    let url = `/api/memories/${id}`
    if (target?.scope === "global")
      url = `/api/global/memories/${id}`
    else if (target?.scope === "project" && target.project?.root)
      url = `/api/project/memories/${id}?projectRoot=${encodeURIComponent(target.project.root)}`
    const res = await fetch(url, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    })
    if (res.ok) {
      const updated = (await res.json()) as Memory
      setMemories((x) =>
        x.map((y) =>
          y.id === id
            ? { ...updated, scope: target?.scope, project: target?.project }
            : y
        )
      )
      setEditingId(null)
      scrollToTop()
    }
  }

  async function promote(m: Memory, move = false) {
    if (m.scope !== "project" || !m.project?.root) return
    const res = await fetch("/api/scoped/promote-to-global", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id: m.id, sourceProjectRoot: m.project.root, move }),
    })
    if (res.ok) {
      // reload list so the user sees the new global entry (or the removed source on move)
      await load()
      scrollToTop()
    } else {
      setError(`Promote failed: ${res.status}`)
    }
  }

  async function copyToProject(m: Memory, targetRoot: string, move = false) {
    const isGlobal = m.scope === "global"
    const body = isGlobal
      ? { id: m.id, sourceScope: "global", targetProjectRoot: targetRoot, move }
      : {
          id: m.id,
          sourceScope: "project",
          sourceProjectRoot: m.project?.root,
          targetProjectRoot: targetRoot,
          move,
        }
    const res = await fetch("/api/scoped/copy-to-project", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    })
    if (res.ok) {
      await load()
      scrollToTop()
    } else {
      setError(`Copy failed: ${res.status}`)
    }
  }

  const normalizedQuery = query.trim().toLowerCase()

  const filtered = memories
    .filter(
      (m) =>
        (type === "All" || m.type === type) &&
        (!normalizedQuery ||
          m.content.toLowerCase().includes(normalizedQuery) ||
          m.id.toLowerCase().includes(normalizedQuery))
    )
    .sort((a, b) => {
      const timeA = new Date(a.updatedAt || a.createdAt).getTime()
      const timeB = new Date(b.updatedAt || b.createdAt).getTime()
      if (timeB !== timeA) return timeB - timeA
      // Fallback to ULID string comparison (monotonic descending)
      return b.id.localeCompare(a.id)
    })

  return (
    <>
      <header className="flex h-16 shrink-0 items-center justify-between gap-2 px-4 border-b border-border/40">
        <div className="flex items-center gap-2">
          <SidebarTrigger className="-ml-1" />
          <Separator
            orientation="vertical"
            className="mr-2 data-vertical:h-4 data-vertical:self-auto"
          />
          <Breadcrumb>
            <BreadcrumbList>
              <BreadcrumbItem className="hidden md:block">
                <BreadcrumbLink href="#">Eling</BreadcrumbLink>
              </BreadcrumbItem>
              <BreadcrumbSeparator className="hidden md:block" />
              <BreadcrumbItem>
                <BreadcrumbPage>Memories</BreadcrumbPage>
              </BreadcrumbItem>
            </BreadcrumbList>
          </Breadcrumb>
        </div>

        <div className="flex items-center gap-2">
          <div
            className="flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[11px] font-medium border border-border/50 bg-muted/30"
            title={
              sseStatus === "connected"
                ? "SSE Connected: Live stream active"
                : "SSE: Connecting..."
            }
          >
            <span
              className={`inline-block size-2 rounded-full ${
                sseStatus === "connected"
                  ? "bg-green-500 animate-pulse"
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

          <span className="hidden sm:inline-block text-xs text-muted-foreground font-medium">
            {filtered.length} of {memories.length}
          </span>

          <Button
            variant="outline"
            size="sm"
            onClick={async () => {
              setLoading(true)
              setError(null)
              await load("manual_click")
            }}
            disabled={mounted ? loading : false}
          >
            <RefreshCw
              className={mounted && loading ? "size-4 animate-spin" : "size-4"}
            />
            <span className="hidden sm:inline-block">Refresh</span>
          </Button>

          <Button
            size="sm"
            onClick={() => router.push("/dashboard/create/")}
          >
            <Plus className="size-4" />
            <span>New Memory</span>
          </Button>
        </div>
      </header>

      <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          {loading ? (
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-20 rounded-lg" />
            ))
          ) : (
            <>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Total</div>
                <div className="text-xl font-medium">{memories.length}</div>
              </div>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Showing</div>
                <div className="text-xl font-medium">{filtered.length}</div>
              </div>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Scope</div>
                <div className="truncate text-sm" title={scope}>
                  {scopeLabel(scope)}
                </div>
              </div>
            </>
          )}
        </div>

        <div className="truncate text-xs text-muted-foreground" title={scopes.map((s) => s.root).join("\n")}>
          {scopes.length} {scopes.length === 1 ? "scope" : "scopes"} available
        </div>

        <div className="sticky top-0 z-10 flex flex-col gap-2 bg-background pb-2 pt-1">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="flex min-w-0 flex-1 items-center gap-2">
              <Input
                placeholder="Search content or memory ID..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="max-w-xs"
              />
              <Select value={scope} onValueChange={handleScopeChange}>
                <SelectTrigger
                  aria-label="Memory scope"
                  title={scope}
                  className="w-full max-w-44 shrink-0 truncate sm:max-w-72"
                >
                  <SelectValue placeholder="Select scope...">
                    {(v: string | null) => scopeLabel(v)}
                  </SelectValue>
                </SelectTrigger>
                <SelectContent
                  side="bottom"
                  align="start"
                  alignItemWithTrigger={false}
                  style={{
                    width: "auto",
                    maxWidth: "min(36rem, calc(100vw - 2rem))",
                  }}
                >
                  <SelectItem value="all">🌐 All projects</SelectItem>
                  <SelectItem value="global">🌐 Global Scope</SelectItem>
                  {scopes.map((s) => (
                    <SelectItem key={s.root.toLowerCase()} value={s.root} title={s.root}>
                      <span className="flex min-w-0 flex-col items-start">
                        <span>📁 {s.name}</span>
                        <span className="text-xs whitespace-normal break-all text-muted-foreground">
                          {s.root}
                        </span>
                      </span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-wrap gap-1">
              {TYPES.map((t) => (
                <button
                  key={t}
                  onClick={() => setType(t)}
                  className={
                    type === t
                      ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground"
                      : "rounded-md px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                  }
                >
                  {t}
                </button>
              ))}
            </div>
          </div>
        </div>

        {error && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
            {error}
          </div>
        )}

        {loading ? (
          <div className="flex flex-col gap-2">
            {Array.from({ length: 5 }).map((_, i) => (
              <Skeleton key={i} className="h-20 w-full rounded-xl" />
            ))}
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex min-h-[200px] items-center justify-center rounded-xl border border-dashed text-sm text-muted-foreground">
            No memories found.
          </div>
        ) : (
          <div className="grid grid-cols-1 gap-2.5 lg:grid-cols-2">
            {filtered.map((m) =>
              editingId === m.id ? (
                <MemoryEditor
                  key={m.id}
                  memory={m}
                  onCancel={() => setEditingId(null)}
                  onSave={(body) => save(m.id, body)}
                />
              ) : (
                <MemoryCard
                  key={m.id}
                  memory={m}
                  copiedId={copiedId}
                  canCopyToProject={copyTargets.length > 0}
                  onCopy={copyToClipboard}
                  onEdit={(id) => setEditingId(id)}
                  onDelete={(target) => setDeleteTarget(target)}
                  onPromote={(target) => setPromoteTarget(target)}
                  onCopyToProject={(m, root) => {
                    setCopyTarget(m)
                    setCopyProjectRoot(root)
                    setCopyAsMove(false)
                  }}
                />
              )
            )}
          </div>
        )}
      </div>

      <AlertDialog
        open={promoteTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setPromoteTarget(null)
            setPromoteAsMove(false)
          }
        }}
      >
        <AlertDialogContent
          className="w-[94vw] sm:max-w-4xl"
          onOverlayClick={() => {
            setPromoteTarget(null)
            setPromoteAsMove(false)
          }}
        >
          <AlertDialogHeader>
            <AlertDialogTitle>Promote to Global Scope</AlertDialogTitle>
            <AlertDialogDescription>
              Review the memory details and choose the promotion mode below:
            </AlertDialogDescription>
          </AlertDialogHeader>

          {promoteTarget && (
            <div className="rounded-lg border bg-muted/40 p-4 text-xs text-muted-foreground space-y-2">
              <div className="flex items-center justify-between font-medium text-foreground border-b border-border/40 pb-2">
                <span>
                  {promoteTarget.type} · ID: {promoteTarget.id}
                </span>
                <span className="text-[11px] font-normal text-muted-foreground">
                  {utcToLocal(promoteTarget.createdAt)}
                </span>
              </div>
              <div className="max-h-72 overflow-y-auto whitespace-pre-wrap break-words text-foreground font-sans pr-1 leading-relaxed">
                {promoteTarget.content}
              </div>
            </div>
          )}

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 py-1 text-xs">
            <label className="flex items-start gap-2.5 rounded-lg border p-3.5 cursor-pointer hover:bg-accent transition-colors">
              <input
                type="radio"
                name="promote-mode"
                checked={!promoteAsMove}
                onChange={() => setPromoteAsMove(false)}
                className="mt-0.5"
              />
              <div className="flex-1">
                <div className="font-medium text-foreground">
                  Copy to Global (Recommended)
                </div>
                <div className="text-muted-foreground mt-1">
                  Original memory stays in this project; a new copy is created
                  in Global scope.
                </div>
              </div>
            </label>

            <label className="flex items-start gap-2.5 rounded-lg border p-3.5 cursor-pointer hover:bg-accent transition-colors">
              <input
                type="radio"
                name="promote-mode"
                checked={promoteAsMove}
                onChange={() => setPromoteAsMove(true)}
                className="mt-0.5"
              />
              <div className="flex-1">
                <div className="font-medium text-foreground text-amber-600 dark:text-amber-400">
                  Move to Global
                </div>
                <div className="text-muted-foreground mt-1">
                  Original memory will be deleted from this project and moved
                  permanently to Global scope.
                </div>
              </div>
            </label>
          </div>

          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className={
                promoteAsMove ? "bg-amber-600 hover:bg-amber-700 text-white" : ""
              }
              onClick={() => {
                if (promoteTarget) {
                  const target = promoteTarget
                  const asMove = promoteAsMove
                  setPromoteTarget(null)
                  setPromoteAsMove(false)
                  void promote(target, asMove)
                }
              }}
            >
              {promoteAsMove ? "Move to Global" : "Copy to Global"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog
        open={copyTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setCopyTarget(null)
            setCopyAsMove(false)
            setCopyProjectRoot("")
          }
        }}
      >
        <AlertDialogContent
          className="w-[94vw] sm:max-w-4xl"
          onOverlayClick={() => {
            setCopyTarget(null)
            setCopyAsMove(false)
            setCopyProjectRoot("")
          }}
        >
          <AlertDialogHeader>
            <AlertDialogTitle>Copy to Project</AlertDialogTitle>
            <AlertDialogDescription>
              Choose how to copy this memory to the target project:
            </AlertDialogDescription>
          </AlertDialogHeader>

          <div className="space-y-2">
            <label className="text-xs font-medium text-foreground">Target Project</label>
            <Select value={copyProjectRoot} onValueChange={(v) => setCopyProjectRoot(v ?? "")}>
              <SelectTrigger className="w-full">
                <SelectValue placeholder="Select a project..." />
              </SelectTrigger>
              <SelectContent>
                {copyTargets.map((s) => (
                  <SelectItem key={s.root.toLowerCase()} value={s.root}>
                    📁 {s.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {copyTarget && (
            <div className="rounded-lg border bg-muted/40 p-4 text-xs text-muted-foreground space-y-2">
              <div className="flex items-center justify-between font-medium text-foreground border-b border-border/40 pb-2">
                <span>
                  {copyTarget.type} · ID: {copyTarget.id}
                </span>
                <span className="text-[11px] font-normal text-muted-foreground">
                  {utcToLocal(copyTarget.createdAt)}
                </span>
              </div>
              <div className="max-h-72 overflow-y-auto whitespace-pre-wrap break-words text-foreground font-sans pr-1 leading-relaxed">
                {copyTarget.content}
              </div>
            </div>
          )}

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 py-1 text-xs">
            <label className="flex items-start gap-2.5 rounded-lg border p-3.5 cursor-pointer hover:bg-accent transition-colors">
              <input
                type="radio"
                name="copy-mode"
                checked={!copyAsMove}
                onChange={() => setCopyAsMove(false)}
                className="mt-0.5"
              />
              <div className="flex-1">
                <div className="font-medium text-foreground">
                  Copy to Project (Recommended)
                </div>
                <div className="text-muted-foreground mt-1">
                  Original memory stays in {copyTarget?.scope === "global" ? "Global" : "this project"}; a new copy is created in the target project.
                </div>
              </div>
            </label>

            <label className="flex items-start gap-2.5 rounded-lg border p-3.5 cursor-pointer hover:bg-accent transition-colors">
              <input
                type="radio"
                name="copy-mode"
                checked={copyAsMove}
                onChange={() => setCopyAsMove(true)}
                className="mt-0.5"
              />
              <div className="flex-1">
                <div className="font-medium text-foreground text-amber-600 dark:text-amber-400">
                  Move to Project
                </div>
                <div className="text-muted-foreground mt-1">
                  Original memory will be deleted from {copyTarget?.scope === "global" ? "Global" : "this project"} and moved permanently to the target project.
                </div>
              </div>
            </label>
          </div>

          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className={
                copyAsMove ? "bg-amber-600 hover:bg-amber-700 text-white" : ""
              }
              onClick={() => {
                if (copyTarget && copyProjectRoot) {
                  const target = copyTarget
                  const root = copyProjectRoot
                  const asMove = copyAsMove
                  setCopyTarget(null)
                  setCopyAsMove(false)
                  setCopyProjectRoot("")
                  void copyToProject(target, root, asMove)
                }
              }}
            >
              {copyAsMove ? "Move to Project" : "Copy to Project"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => !open && setDeleteTarget(null)}
      >
        <AlertDialogContent
          className="w-[94vw] sm:max-w-4xl"
          onOverlayClick={() => setDeleteTarget(null)}
        >
          <AlertDialogHeader>
            <AlertDialogTitle className="text-destructive">
              Delete Memory?
            </AlertDialogTitle>
            <AlertDialogDescription>
              This action will permanently delete this memory from storage disk.
              This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {deleteTarget && (
            <div className="rounded-lg border border-destructive/20 bg-destructive/5 p-4 text-xs text-muted-foreground space-y-2">
              <div className="flex items-center justify-between font-medium text-foreground border-b border-destructive/10 pb-2">
                <span>
                  {deleteTarget.type} · ID: {deleteTarget.id}
                </span>
                <span className="text-[11px] font-normal text-muted-foreground">
                  {utcToLocal(deleteTarget.createdAt)}
                </span>
              </div>
              <div className="max-h-72 overflow-y-auto whitespace-pre-wrap break-words text-foreground font-sans pr-1 leading-relaxed">
                {deleteTarget.content}
              </div>
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => {
                if (deleteTarget) {
                  const target = deleteTarget
                  setDeleteTarget(null)
                  void remove(target)
                }
              }}
            >
              Delete Permanently
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}