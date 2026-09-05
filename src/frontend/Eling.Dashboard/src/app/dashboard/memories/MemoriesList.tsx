"use client"

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react"
import { useRouter } from "next/navigation"
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
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import { formatDate } from "@/lib/date-utils"
import { TYPES } from "@/lib/types"
import type { Memory, Runtime } from "@/lib/types"

import { MemoryCard } from "./MemoryCard"
import { MemoryEditor } from "./MemoryEditor"

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

  const [copiedId, setCopiedId] = useState<string | null>(null)
  const [promoteTarget, setPromoteTarget] = useState<Memory | null>(null)
  const [promoteAsMove, setPromoteAsMove] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<Memory | null>(null)
  const [scope, setScope] = useState<string>("all")
  const [runtimes, setRuntimes] = useState<Runtime[]>([])

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

  const loadRuntimes = useCallback(async () => {
    try {
      const res = await fetch(`/api/coordinator/runtimes?_t=${Date.now()}`, {
        cache: "no-store",
        headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
      })
      if (res.ok) setRuntimes(await res.json())
    } catch {
      // ignore
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

  // Fetch active runtimes and memories on mount / scope change
  useEffect(() => {
    let isMounted = true

    const fetchAll = async () => {
      await loadRuntimes()
      if (isMounted) {
        await load("mount_or_scope_change")
      }
    }

    void fetchAll()

    const onFocus = () => void loadRuntimes()
    window.addEventListener("focus", onFocus)
    const onVisibility = () => {
      if (document.visibilityState === "visible") void loadRuntimes()
    }
    window.addEventListener("visibilitychange", onVisibility)

    return () => {
      isMounted = false
      window.removeEventListener("focus", onFocus)
      window.removeEventListener("visibilitychange", onVisibility)
    }
  }, [load, loadRuntimes])

  // Real-time memory refresh via Server-Sent Events (extracted to the shared hook).
  const { status: sseStatus } = useMemoriesSse(
    useCallback(() => {
      void load("sse_mutation")
    }, [load])
  )

  // Auto-refresh scope list + memories when a new project registers (poll diff)
  useEffect(() => {
    let prev = runtimes.map((r) => r.projectRoot).join("|")
    const check = async () => {
      try {
        const res = await fetch("/api/coordinator/runtimes")
        if (!res.ok) return
        const next: Runtime[] = await res.json()
        const nextKey = next.map((r) => r.projectRoot).join("|")
        if (nextKey !== prev) {
          prev = nextKey
          setRuntimes(next)
          // if currently on "All" or a project that just appeared, reload memories
          void load()
        }
      } catch {
        // ignore
      }
    }
    const id = setInterval(check, 5000)
    return () => clearInterval(id)
  }, [runtimes, load])

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

  async function copyToProject(m: Memory, targetRoot: string) {
    const isGlobal = m.scope === "global"
    const body = isGlobal
      ? { id: m.id, sourceScope: "global", targetProjectRoot: targetRoot }
      : {
          id: m.id,
          sourceScope: "project",
          sourceProjectRoot: m.project?.root,
          targetProjectRoot: targetRoot,
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
      {/* Status + refresh bar — relocated here because the top header is now the
          RSC shell's Breadcrumb shell. Identical layout/behavior as the original
          right-side of the header, wraps under the breadcrumb on narrow screens. */}
      <div className="flex flex-wrap items-center gap-2 px-4 pt-2 pb-0">
        <div
          className="flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium border border-border/50 bg-muted/30"
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

        <span className="text-xs text-muted-foreground">
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
          Refresh
        </Button>
        <Button
          size="sm"
          className="ml-auto"
          onClick={() => router.push("/dashboard/create/")}
        >
          <Plus className="size-4" />
          New Memory
        </Button>
      </div>

      <div className="flex flex-1 flex-col gap-4 p-4 pt-2">
        <div className="sticky top-16 z-10 flex flex-col gap-2 bg-background pb-2">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-medium text-muted-foreground">
              Scope:
            </span>
            <button
              onClick={() => setScope("all")}
              className={
                scope === "all"
                  ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground"
                  : "rounded-md border px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent"
              }
            >
              All Open Projects
            </button>
            <button
              onClick={() => setScope("global")}
              className={
                scope === "global"
                  ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground"
                  : "rounded-md border px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent"
              }
            >
              🌐 Global
            </button>
            {runtimes.map((r) => (
              <button
                key={r.projectRoot}
                onClick={() => setScope(r.projectRoot)}
                className={
                  scope === r.projectRoot
                    ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground"
                    : "rounded-md border px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent"
                }
                title={r.projectRoot}
              >
                📁 {r.projectRoot.split("\\").pop() ?? r.projectRoot.split("/").pop()}
              </button>
            ))}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Input
              placeholder="Search content or memory ID..."
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="max-w-xs"
            />
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
          <div className="grid grid-cols-1 gap-2.5 sm:grid-cols-2">
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
                  runtimes={runtimes}
                  onCopy={copyToClipboard}
                  onEdit={(id) => setEditingId(id)}
                  onDelete={(target) => setDeleteTarget(target)}
                  onPromote={(target) => setPromoteTarget(target)}
                  onCopyToProject={copyToProject}
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
                  {formatDate(promoteTarget.createdAt)}
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
                  {formatDate(deleteTarget.createdAt)}
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