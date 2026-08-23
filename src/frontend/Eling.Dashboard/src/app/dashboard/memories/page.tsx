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
}

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

  const load = useCallback(async () => {
    try {
      const res = await fetch("/api/memories?limit=100")
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      setMemories(await res.json())
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load memories")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    // Mount fetch is intentional; all setStates inside load() run after await,
    // but the rule cannot see through the function boundary.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    load()
  }, [load])

  async function remove(id: string) {
    const res = await fetch(`/api/memories/${id}`, { method: "DELETE" })
    if (res.ok || res.status === 404) {
      setMemories((m) => m.filter((x) => x.id !== id))
    }
  }

  async function save(
    id: string,
    body: { content: string; type: string; status: string }
  ) {
    const res = await fetch(`/api/memories/${id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    })
    if (res.ok) {
      const updated = (await res.json()) as Memory
      setMemories((m) => m.map((x) => (x.id === id ? updated : x)))
      setEditingId(null)
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
        <div className="sticky top-16 z-10 flex flex-wrap items-center gap-2 bg-background pb-2">
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
                      <span className="text-xs text-muted-foreground">
                        {new Date(m.updatedAt).toLocaleString()}
                      </span>
                    </div>
                  </div>
                  <div className="flex shrink-0 gap-1 opacity-0 transition-opacity group-hover:opacity-100">
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
                      onClick={() => remove(m.id)}
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
