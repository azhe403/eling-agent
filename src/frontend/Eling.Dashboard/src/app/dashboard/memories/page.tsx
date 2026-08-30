"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { useRouter } from "next/navigation"
import { Check, Copy, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react"
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
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
import { Skeleton } from "@/components/ui/skeleton"

type Memory = {
  id: string
  type: string
  status: string
  content: string
  tags: string[]
  createdAt: string
  updatedAt: string
  source?: string | null
  scope?: string
  project?: { id: string; root: string } | null
}

type Runtime = { projectRoot: string; dataDirectory: string }

const TYPES = ["All", "Fact", "Preference", "Decision", "Lesson", "Note"]
const EDIT_TYPES = TYPES.slice(1)
const STATUSES = ["Active", "Superseded", "Archived"]

const typeBadge: Record<string, string> = {
  Fact: "bg-blue-500/10 text-blue-600 dark:text-blue-400",
  Preference: "bg-purple-500/10 text-purple-600 dark:text-purple-400",
  Decision: "bg-amber-500/10 text-amber-600 dark:text-amber-400",
  Lesson: "bg-green-500/10 text-green-600 dark:text-green-400",
  Note: "bg-gray-500/10 text-gray-600 dark:text-gray-400",
}

const statusBadge: Record<string, string> = {
  Active: "bg-green-500/10 text-green-600 dark:text-green-400",
  Superseded: "bg-amber-500/10 text-amber-600 dark:text-amber-400",
  Archived: "bg-gray-500/10 text-gray-500",
}

function formatDate(dateStr?: string | null): string {
  if (!dateStr) return ""
  const d = new Date(dateStr)
  if (isNaN(d.getTime())) return ""
  const yyyy = d.getFullYear()
  const mm = String(d.getMonth() + 1).padStart(2, "0")
  const dd = String(d.getDate()).padStart(2, "0")
  const hh = String(d.getHours()).padStart(2, "0")
  const min = String(d.getMinutes()).padStart(2, "0")
  const ss = String(d.getSeconds()).padStart(2, "0")
  return `${yyyy}-${mm}-${dd} ${hh}:${min}:${ss}`
}

export default function MemoriesPage() {
  const router = useRouter()
  // `mounted` tracks whether we're in the client (after hydration) or still
  // rendering on the server. We initialize via typeof check so the first
  // client render reports mounted=true without needing a setState in an effect.
  const [mounted] = useState(
    typeof window !== "undefined" && typeof document !== "undefined"
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
  const [sseStatus, setSseStatus] = useState<"connected" | "connecting" | "error">("connecting")
  const [lastEvent, setLastEvent] = useState<string | null>(null)

  const copyToClipboard = useCallback((text: string, id: string) => {
    navigator.clipboard.writeText(text)
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
    } catch { /* ignore */ }
  }, [])

  const load = useCallback(async (triggeredBy?: string) => {
    try {
      const t = Date.now()
      let url = `/api/aggregated/memories?limit=100&_t=${t}`
      if (scope === "global") url = `/api/global/memories?limit=100&_t=${t}`
      else if (scope !== "all") url = `/api/project/memories?projectRoot=${encodeURIComponent(scope)}&limit=100&_t=${t}`
      
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
            scope: (m.scope as string) ?? (scope === "global" ? "global" : scope === "all" ? m.scope ?? "project" : "project"),
            project: m.project ?? (scope !== "all" && scope !== "global" ? { id: scope.split("\\").pop() ?? scope, root: scope } : null),
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
  }, [scope])

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

  // Real-time memory refresh via Server-Sent Events (SSE)
  useEffect(() => {
    if (typeof window === "undefined") return
    let es: EventSource | null = null
    try {
      console.log(
        "%c[SSE INIT] 🔄 Connecting to /api/events/memories...",
        "color: #d97706; font-weight: bold;"
      )
      es = new EventSource("/api/events/memories")
      
      es.onopen = () => {
        setSseStatus("connected")
        console.log(
          "%c[SSE CONNECTED] 🟢 Live event stream connected to /api/events/memories",
          "color: #16a34a; font-weight: bold; background: #dcfce7; padding: 2px 6px; border-radius: 4px;"
        )
      }

      es.onmessage = (event) => {
        console.log(
          `%c[SSE EVENT RECEIVED] ⚡ Data payload: "${event.data}" at ${new Date().toLocaleTimeString()}`,
          "color: #2563eb; font-weight: bold; background: #dbeafe; padding: 2px 6px; border-radius: 4px;"
        )
        setLastEvent(`Event: ${event.data} (${new Date().toLocaleTimeString()})`)
        // Refresh memories whenever a mutation event is received (silent background refresh)
        if (event.data && event.data !== "connected") {
          console.log("%c[AUTO REFRESH] 🚀 Triggering load('sse_mutation')...", "color: #9333ea; font-weight: bold;")
          void load("sse_mutation")
        }
      }

      es.onerror = () => {
        // EventSource auto-reconnects natively when readyState === 0 (CONNECTING)
        if (es?.readyState === EventSource.CONNECTING) {
          setSseStatus("connecting")
          console.log(
            "%c[SSE RECONNECTING] 🟡 Connection lost, browser attempting auto-reconnect...",
            "color: #d97706; font-weight: bold;"
          )
        } else if (es?.readyState === EventSource.CLOSED) {
          setSseStatus("error")
          console.log(
            "%c[SSE CLOSED] ⚪ EventSource connection closed",
            "color: #6b7280; font-weight: bold;"
          )
        }
      }
    } catch (e) {
      console.error("[SSE INIT ERROR]", e)
    }

    return () => {
      console.log("[SSE CLEANUP] Closing EventSource connection")
      es?.close()
    }
  }, [load])

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
          // if currently on "All" or a project that just appeared, reload memories to reflect new scope
          load()
        }
      } catch { /* ignore */ }
    }
    const id = setInterval(check, 5000)
    return () => clearInterval(id)
  }, [runtimes, load])

  async function remove(m: Memory) {
    let url = `/api/memories/${m.id}`
    if (m.scope === "global") url = `/api/global/memories/${m.id}`
    else if (m.scope === "project" && m.project?.root) url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(m.project.root)}`
    else if (scope !== "all" && scope !== "global") url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(scope)}`
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
    if (target?.scope === "global") url = `/api/global/memories/${id}`
    else if (target?.scope === "project" && target.project?.root) url = `/api/project/memories/${id}?projectRoot=${encodeURIComponent(target.project.root)}`
    const res = await fetch(url, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    })
    if (res.ok) {
      const updated = (await res.json()) as Memory
      setMemories((x) => x.map((y) => (y.id === id ? { ...updated, scope: target?.scope, project: target?.project } : y)))
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
      : { id: m.id, sourceScope: "project", sourceProjectRoot: m.project?.root, targetProjectRoot: targetRoot }
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
      <header className="sticky top-0 z-10 flex h-16 shrink-0 items-center gap-2 bg-background px-4">
        <SidebarTrigger className="-ml-1" />
        <Separator
          orientation="vertical"
          className="mr-2 data-vertical:h-4 data-vertical:self-auto"
        />
        <h1 className="text-sm font-medium">Memories</h1>
        <div className="ml-auto flex items-center gap-2">
          <div
            className="flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium border border-border/50 bg-muted/30"
            title={lastEvent || (sseStatus === "connected" ? "SSE Connected: Live stream active" : "SSE: Connecting...")}
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
              {sseStatus === "connected" ? "Live" : sseStatus === "connecting" ? "Connecting" : "Offline"}
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
            <RefreshCw className={mounted && loading ? "size-4 animate-spin" : "size-4"} />
            Refresh
          </Button>
          <Button size="sm" onClick={() => router.push("/dashboard/create/")}>
            <Plus className="size-4" />
            New Memory
          </Button>
        </div>
      </header>

      <div className="flex flex-1 flex-col gap-4 p-4 pt-0">
        <div className="sticky top-16 z-10 flex flex-col gap-2 bg-background pb-2">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-medium text-muted-foreground">Scope:</span>
            <button
              onClick={() => setScope("all")}
              className={scope === "all" ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground" : "rounded-md border px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent"}
            >
              All Open Projects
            </button>
            <button
              onClick={() => setScope("global")}
              className={scope === "global" ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground" : "rounded-md border px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent"}
            >
              🌐 Global
            </button>
            {runtimes.map((r) => (
              <button
                key={r.projectRoot}
                onClick={() => setScope(r.projectRoot)}
                className={scope === r.projectRoot ? "rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground" : "rounded-md border px-3 py-1.5 text-xs font-medium text-muted-foreground hover:bg-accent"}
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
                <EditCard
                  key={m.id}
                  memory={m}
                  onCancel={() => setEditingId(null)}
                  onSave={(body) => save(m.id, body)}
                />
              ) : (
                <div
                  key={m.id}
                  className="group flex items-start gap-3 rounded-xl border bg-card p-4"
                >
                  <div className="flex min-w-0 flex-1 flex-col gap-2">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <div className="flex flex-wrap items-center gap-1.5 text-xs">
                        <span
                          className={`rounded-md px-2 py-0.5 font-medium ${typeBadge[m.type] ?? typeBadge.Note}`}
                        >
                          {m.type}
                        </span>
                        {m.status !== "Active" && (
                          <span
                            className={`rounded-md px-2 py-0.5 font-medium ${statusBadge[m.status] ?? statusBadge.Archived}`}
                          >
                            {m.status}
                          </span>
                        )}
                        <span className={m.scope === "global" ? "rounded-md bg-blue-500/10 px-2 py-0.5 font-medium text-blue-600" : "rounded-md bg-amber-500/10 px-2 py-0.5 font-medium text-amber-600"}>
                          {m.scope === "global" ? "🌐 Global" : m.project ? `📁 ${m.project.id}` : "📁 Project"}
                        </span>
                      </div>
                    </div>

                    <p className="max-w-2xl whitespace-pre-wrap break-words text-sm">
                      {m.content}
                    </p>

                    <div className="flex flex-wrap items-center gap-1.5 text-xs">
                      {m.tags?.map((tag) => (
                        <span
                          key={tag}
                          className="rounded-md bg-secondary px-2 py-0.5 text-secondary-foreground"
                        >
                          #{tag}
                        </span>
                      ))}
                    </div>

                    <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1 text-[11px] text-muted-foreground">
                      <button
                        onClick={() => copyToClipboard(m.id, m.id)}
                        className="inline-flex items-center gap-1 font-mono text-[11px] hover:text-foreground transition-colors cursor-pointer bg-muted/50 hover:bg-muted rounded px-1.5 py-0.5"
                        title={`Copy full ID: ${m.id}`}
                      >
                        <span>ID: {m.id.length > 12 ? `${m.id.slice(0, 6)}…${m.id.slice(-4)}` : m.id}</span>
                        {copiedId === m.id ? (
                          <Check className="size-3 text-green-500" />
                        ) : (
                          <Copy className="size-3 opacity-60 hover:opacity-100" />
                        )}
                      </button>

                      <span>•</span>
                      <span>Created: {formatDate(m.createdAt)}</span>
                      <span>•</span>
                      <span>Updated: {formatDate(m.updatedAt || m.createdAt)}</span>
                    </div>
                  </div>
                  <div className="flex shrink-0 gap-1">
                    {m.scope === "project" && (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-muted-foreground hover:text-foreground text-xs"
                        onClick={() => setPromoteTarget(m)}
                        aria-label="Promote to global"
                        title="Promote to Global (copy, original stays)"
                      >
                        ↑ Global
                      </Button>
                    )}
                    {m.scope === "global" && runtimes.length > 0 && (
                      <select
                        aria-label="Copy to project"
                        title="Copy to Project"
                        className="h-8 rounded-md border bg-background px-2 text-xs"
                        defaultValue=""
                        onChange={(e) => {
                          if (e.target.value) {
                            copyToProject(m, e.target.value)
                            e.target.value = ""
                          }
                        }}
                      >
                        <option value="" disabled>
                          Copy to…
                        </option>
                        {runtimes.map((r) => (
                          <option key={r.projectRoot} value={r.projectRoot}>
                            📁 {r.projectRoot.split("\\").pop() ?? r.projectRoot.split("/").pop()}
                          </option>
                        ))}
                      </select>
                    )}
                    <Button
                      variant="ghost"
                      size="icon"
                      className="text-muted-foreground hover:text-foreground"
                      onClick={() => setEditingId(m.id)}
                      aria-label="Edit memory"
                    >
                      <Pencil className="size-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="text-muted-foreground hover:text-destructive"
                      onClick={() => setDeleteTarget(m)}
                      aria-label="Delete memory"
                    >
                      <Trash2 className="size-4" />
                    </Button>
                  </div>
                </div>
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
                <span>{promoteTarget.type} · ID: {promoteTarget.id}</span>
                <span className="text-[11px] font-normal text-muted-foreground">{formatDate(promoteTarget.createdAt)}</span>
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
                <div className="font-medium text-foreground">Copy to Global (Recommended)</div>
                <div className="text-muted-foreground mt-1">Original memory stays in this project; a new copy is created in Global scope.</div>
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
                <div className="font-medium text-foreground text-amber-600 dark:text-amber-400">Move to Global</div>
                <div className="text-muted-foreground mt-1">Original memory will be deleted from this project and moved permanently to Global scope.</div>
              </div>
            </label>
          </div>

          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className={promoteAsMove ? "bg-amber-600 hover:bg-amber-700 text-white" : ""}
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
            <AlertDialogTitle className="text-destructive">Delete Memory?</AlertDialogTitle>
            <AlertDialogDescription>
              This action will permanently delete this memory from storage disk. This cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {deleteTarget && (
            <div className="rounded-lg border border-destructive/20 bg-destructive/5 p-4 text-xs text-muted-foreground space-y-2">
              <div className="flex items-center justify-between font-medium text-foreground border-b border-destructive/10 pb-2">
                <span>{deleteTarget.type} · ID: {deleteTarget.id}</span>
                <span className="text-[11px] font-normal text-muted-foreground">{formatDate(deleteTarget.createdAt)}</span>
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

function EditCard({
  memory,
  onCancel,
  onSave,
}: {
  memory: Memory
  onCancel: () => void
  onSave: (body: { content: string; type: string; status: string }) => void
}) {
  const [content, setContent] = useState(memory.content)
  const [type, setType] = useState(memory.type)
  const [status, setStatus] = useState(memory.status)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const adjustHeight = useCallback(() => {
    if (textareaRef.current) {
      textareaRef.current.style.height = "auto"
      // N+1 line buffer (24px) for elegant breathing room below the last line
      const nextHeight = Math.max(80, textareaRef.current.scrollHeight + 24)
      textareaRef.current.style.height = `${nextHeight}px`
    }
  }, [])

  useEffect(() => {
    adjustHeight()
  }, [adjustHeight])

  return (
    <div className="flex flex-col gap-3 rounded-xl border border-primary/40 bg-card p-4 shadow-sm">
      <div className="flex items-center justify-between text-xs text-muted-foreground border-b border-border/40 pb-2">
        <span className="font-medium text-foreground">Editing Memory · ID: {memory.id}</span>
        <span>Created: {formatDate(memory.createdAt)}</span>
      </div>
      <textarea
        ref={textareaRef}
        value={content}
        onChange={(e) => {
          setContent(e.target.value)
          adjustHeight()
        }}
        placeholder="Enter memory content..."
        className="w-full rounded-lg border border-input bg-transparent px-3 py-2 text-sm font-sans leading-relaxed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y min-h-[80px] overflow-hidden"
      />
      <div className="flex flex-wrap items-center gap-2">
        <select
          value={type}
          onChange={(e) => setType(e.target.value)}
          className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {EDIT_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value)}
          className="h-9 rounded-md border border-input bg-transparent px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <div className="ml-auto flex gap-2">
          <Button size="sm" onClick={() => onSave({ content, type, status })} disabled={!content.trim()}>
            Save
          </Button>
          <Button size="sm" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </div>
    </div>
  )
}
