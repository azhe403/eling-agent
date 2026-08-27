"use client"

import { useCallback, useEffect, useState } from "react"
import { useRouter } from "next/navigation"
import { Pencil, Plus, RefreshCw, Trash2 } from "lucide-react"
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

export default function MemoriesPage() {
  const router = useRouter()
  const [memories, setMemories] = useState<Memory[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [type, setType] = useState("All")
  const [query, setQuery] = useState("")
  const [editingId, setEditingId] = useState<string | null>(null)
  const [scope, setScope] = useState<string>("all")
  const [runtimes, setRuntimes] = useState<Runtime[]>([])

  const loadRuntimes = useCallback(async () => {
    try {
      const res = await fetch("/api/coordinator/runtimes")
      if (res.ok) setRuntimes(await res.json())
    } catch { /* ignore */ }
  }, [])

  const load = useCallback(async () => {
    try {
      let url = "/api/aggregated/memories?limit=100"
      if (scope === "global") url = "/api/global/memories?limit=100"
      else if (scope !== "all") url = `/api/project/memories?projectRoot=${encodeURIComponent(scope)}&limit=100`
      const res = await fetch(url)
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const data = await res.json()
      // Dashboard scoped endpoints return {id, type, status, content, tags, createdAt, updatedAt, scope, project}
      // Legacy /api/memories returns flat Memory; normalize to include scope
      const normalized: Memory[] = Array.isArray(data)
        ? data.map((m: Memory) => ({
            ...m,
            scope: (m.scope as string) ?? (scope === "global" ? "global" : scope === "all" ? m.scope ?? "project" : "project"),
            project: m.project ?? (scope !== "all" && scope !== "global" ? { id: scope.split("\\").pop() ?? scope, root: scope } : null),
          }))
        : []
      setMemories(normalized)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load memories")
    } finally {
      setLoading(false)
    }
  }, [scope])

  useEffect(() => {
    // Mount fetch is intentional; all setStates inside loadRuntimes() run
    // after await, but the rule cannot see through the function boundary.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    loadRuntimes()
    const onFocus = () => loadRuntimes()
    window.addEventListener("focus", onFocus)
    window.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") loadRuntimes()
    })
    return () => {
      window.removeEventListener("focus", onFocus)
    }
  }, [loadRuntimes])

  useEffect(() => {
    // Mount fetch is intentional; all setStates inside load() run after await,
    // but the rule cannot see through the function boundary.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    load()
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
    }
  }

  async function promote(m: Memory) {
    if (m.scope !== "project" || !m.project?.root) return
    const res = await fetch("/api/scoped/promote-to-global", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id: m.id, sourceProjectRoot: m.project.root }),
    })
    if (res.ok) {
      // keep original, just toast via reload
      await load()
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
    } else {
      setError(`Copy failed: ${res.status}`)
    }
  }

  const filtered = memories.filter(
    (m) =>
      (type === "All" || m.type === type) &&
      (!query || m.content.toLowerCase().includes(query.toLowerCase()))
  )

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
          <span className="text-xs text-muted-foreground">
            {filtered.length} of {memories.length}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={async () => {
              setLoading(true)
              setError(null)
              await load()
            }}
            disabled={loading}
          >
            <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
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
              placeholder="Search content..."
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
          <div className="flex flex-col gap-2">
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
                  <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                    <p className="whitespace-pre-wrap break-words text-sm">
                      {m.content}
                    </p>
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span
                        className={`rounded-md px-2 py-0.5 text-xs font-medium ${typeBadge[m.type] ?? typeBadge.Note}`}
                      >
                        {m.type}
                      </span>
                      {m.status !== "Active" && (
                        <span
                          className={`rounded-md px-2 py-0.5 text-xs font-medium ${statusBadge[m.status] ?? statusBadge.Archived}`}
                        >
                          {m.status}
                        </span>
                      )}
                      {m.tags?.map((tag) => (
                        <span
                          key={tag}
                          className="rounded-md bg-secondary px-2 py-0.5 text-xs text-secondary-foreground"
                        >
                          #{tag}
                        </span>
                      ))}
                      <span className={m.scope === "global" ? "rounded-md bg-blue-500/10 px-2 py-0.5 text-xs font-medium text-blue-600" : "rounded-md bg-amber-500/10 px-2 py-0.5 text-xs font-medium text-amber-600"}>
                        {m.scope === "global" ? "🌐 Global" : m.project ? `📁 ${m.project.id}` : "📁 Project"}
                      </span>
                      <span className="text-xs text-muted-foreground">
                        {new Date(m.updatedAt).toLocaleString()}
                      </span>
                    </div>
                  </div>
                  <div className="flex shrink-0 gap-1 opacity-0 transition-opacity group-hover:opacity-100">
                    {m.scope === "project" && (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-muted-foreground hover:text-foreground text-xs"
                        onClick={() => promote(m)}
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
                      onClick={() => remove(m)}
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

  return (
    <div className="flex flex-col gap-3 rounded-xl border bg-card p-4">
      <textarea
        value={content}
        onChange={(e) => setContent(e.target.value)}
        rows={4}
        className="rounded-lg border border-input bg-transparent px-3 py-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
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
