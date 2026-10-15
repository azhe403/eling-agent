"use client"

import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import { Globe, Plus, RefreshCw } from "lucide-react"

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
import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import { utcToLocal } from "@/lib/date-utils"
import { TYPES } from "@/lib/types"
import type { Memory } from "@/lib/types"
import {
  selectionSummary,
  toggleRoot,
} from "@/components/workspaces/selection"
import { useWorkspaceRows } from "@/components/workspaces/use-workspace-rows"
import { WorkspaceScopePanel } from "@/components/workspaces/workspace-scope-panel"
import { WorkspaceSelect } from "@/components/workspaces/workspace-select"
import {
  memoryRequests,
  selectionFromParams,
  selectionQuery,
  selectAllProjects,
  selectGlobalScope,
  selectWorkspaces,
  globalOn,
  tierApplies,
  tierFromParams,
  tierQuery,
  toWorkspaceSelection,
  fromWorkspaceSelection,
  saveLastSelection,
  loadLastSelection,
  type MemoryScope,
  type MemoryTier,
} from "./scope"

import { MemoryCard } from "./MemoryCard"
import { MemoryEditor } from "./MemoryEditor"

type ProjectScope = {
  root: string
  name: string
  writable: boolean
  hasLiveRuntime: boolean
}

/**
 * Copy targets are the scopes this workspace may actually write to. That is a
 * different question from what the picker shows: the picker lists workspaces
 * from the runtime registry (same as the codebase page), while a writable
 * target is any `.eling` scope — including one nobody is running in.
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

function scopeTileLabel(selection: MemoryScope): string {
  if (selection.kind === "all") return "🌐 All projects"
  if (selection.roots.length === 0) return "🌐 Global Scope"
  return (
    "📁 " +
    selectionSummary(toWorkspaceSelection(selection)) +
    (selection.global ? " · 🌐 Global" : "")
  )
}

export function MemoriesList() {
  const router = useRouter()
  const pathname = usePathname()

  // Server + initial client renders always assume desktop/loaded; the real value
  // is applied right after hydration, avoiding a server/client mismatch.
  const mounted = useSyncExternalStore(
    () => () => {},
    () => true,
    () => false
  )

  const [memories, setMemories] = useState<Memory[]>([])
  // The full count for the current selection, before the page window: the
  // backend reports it in the list metadata, so the Total tile never has to
  // guess from what happened to be fetched.
  const [total, setTotal] = useState(0)
  // How many of those survive the current type/query filters — the backend
  // counts it, the Showing tile just shows it.
  const [matching, setMatching] = useState(0)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  // Error banner auto-dismisses so a stale failure (e.g. a transient 404)
  // never sticks around after the next successful load.
  useEffect(() => {
    if (error === null) return
    const t = setTimeout(() => setError(null), 5000)
    return () => clearTimeout(t)
  }, [error])
  const [type, setType] = useState("All")
  const [query, setQuery] = useState("")
  // The search box types faster than a request should go out: 500ms of quiet
  // (the same debounce the codebase page uses) before the query reaches the
  // backend, where the filtering and counting now happen.
  const [debouncedQuery, setDebouncedQuery] = useState(query)
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(query), 500)
    return () => clearTimeout(timer)
  }, [query])
  const [editingId, setEditingId] = useState<string | null>(null)
  const editingIdRef = useRef<string | null>(null)
  useEffect(() => {
    editingIdRef.current = editingId
  }, [editingId])

  const searchParams = useSearchParams()
  // The URL query is the selection's source of truth: no project params means
  // "all", `global=1` the global scope, otherwise the explicit workspace list,
  // so reload, back/forward and shared links all land on the same scope the
  // tiles and the list describe.
  const selection = useMemo(
    () => selectionFromParams(searchParams),
    [searchParams]
  )
  const workspaceSelection = useMemo(
    () => toWorkspaceSelection(selection),
    [selection]
  )
  // Tier only applies to a single-project view: shared (committed), local-only (machine-only), or both.
  const tier = tierFromParams(searchParams, tierApplies(selection))
  // Workspace rows come from the runtime registry — the same list the codebase
  // page's selector shows.
  const { rows, reload } = useWorkspaceRows()
  const allRoots = useMemo(() => rows.map((r) => r.root), [rows])
  const [copiedId, setCopiedId] = useState<string | null>(null)

  // One writer for the selection and its tier: a scope change keeps the tier
  // while it still applies, and any other change drops it (the URL always
  // describes what the list actually read). Every write is remembered too, so
  // a bare revisit resumes this position.
  const writeUrl = useCallback(
    (next: MemoryScope, nextTier?: MemoryTier) => {
      const t = nextTier ?? tier
      const parts = [selectionQuery(next), tierQuery(next, t)].filter(Boolean)
      const qs = parts.join("&")
      saveLastSelection(qs)
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [pathname, router, tier]
  )
  // A bare visit resumes the last position this browser left off at. Any
  // selection param in the URL wins — shared links, reloads of an explicit
  // selection and back/forward keep working; this only fills in an address
  // that carries no selection at all.
  const resumed = useRef(false)
  useEffect(() => {
    if (resumed.current) return
    resumed.current = true
    if (searchParams.toString() !== "") return
    const saved = loadLastSelection()
    if (saved) router.replace(`${pathname}?${saved}`, { scroll: false })
  }, [pathname, router, searchParams])
  // Selection changes flip the skeleton on right in the event handler — before
  // the router round-trip — so the list reacts to the click, not to the next
  // commit. Same pattern as the codebase page.
  const applySelection = useCallback(
    (next: MemoryScope) => {
      setLoading(true)
      writeUrl(next)
    },
    [writeUrl]
  )
  const handleTierChange = useCallback(
    (value: MemoryTier) => {
      if (!tierApplies(selection)) return
      setLoading(true)
      writeUrl(selection, value)
    },
    [selection, writeUrl]
  )
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
        // Every list endpoint answers with metadata alongside the items, and
        // the backend does the filtering and counting now: `total` is the scope
        // size and `matching` is what survives the type/query filters — the
        // two numbers the tiles show. Nothing is filtered or counted here.
        const fetchJson = async (url: string) => {
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
          const items: Memory[] = Array.isArray(data?.items) ? data.items : []
          const total =
            typeof data?.metadata?.total === "number"
              ? data.metadata.total
              : items.length
          const matching =
            typeof data?.metadata?.matching === "number"
              ? data.metadata.matching
              : items.length
          return { items, total, matching }
        }

        const requests = memoryRequests(selection, tier, t, {
          type,
          q: debouncedQuery,
        })
        const batches = await Promise.all(requests.map((r) => fetchJson(r.url)))

        // One call is the whole view: the backend merges every selected
        // workspace and each one's .eling chain, applies the filters and
        // reports both counts — the browser renders the answer.
        const raw: Memory[] = batches.flatMap((b) => b.items)
        const serverTotal = batches.reduce((sum, b) => sum + b.total, 0)
        const serverMatching = batches.reduce((sum, b) => sum + b.matching, 0)

        setMemories(raw)
        setTotal(serverTotal)
        setMatching(serverMatching)
        setError(null)
        console.log(
          `%c[MEMORIES LOADED] ✅ ${raw.length} memories loaded at ${new Date().toLocaleTimeString()}`,
          "color: #16a34a; font-weight: bold;"
        )
      } catch (e) {
        console.error("[FETCH ERROR]", e)
        setError(e instanceof Error ? e.message : "Failed to load memories")
      } finally {
        setLoading(false)
      }
    },
    [selection, tier, type, debouncedQuery]
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

  // Real-time memory and workspace refresh via Server-Sent Events (SSE).
  // A memory write reloads the list. A "runtimes" event means membership
  // changed: the picker's rows reload silently (a failed background refresh
  // keeps the last known rows), the copy targets re-check writability, and the
  // list reloads because membership also changes the "all" aggregate.
  const { status: sseStatus } = useMemoriesSse(
    useCallback(() => {
      void load("sse_mutation")
    }, [load]),
    useCallback(() => {
      void reload({ silent: true })
      void loadScopes()
      void load("sse_runtimes")
    }, [load, loadScopes, reload])
  )

  async function remove(m: Memory) {
    let url = `/api/memories/${m.id}`
    if (m.scope === "global") url = `/api/global/memories/${m.id}`
    else if (m.scope === "project" && m.project?.root)
      url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(m.project.root)}`
    else if (m.scope === "project-local" && m.project?.root)
      url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(m.project.root)}&tier=project-local`
    else if (selection.kind === "selection" && selection.roots.length === 1)
      url = `/api/project/memories/${m.id}?projectRoot=${encodeURIComponent(selection.roots[0])}${tier === "project-local" ? "&tier=project-local" : ""}`
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
    else if (target?.scope === "project-local" && target.project?.root)
      url = `/api/project/memories/${id}?projectRoot=${encodeURIComponent(target.project.root)}&tier=project-local`
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

  async function moveTier(m: Memory, target: "project" | "project-local") {
    if (m.scope === target || !m.project?.root) return
    const root = m.project.root
    const url = target === "project-local" ? "/api/scoped/move-to-local" : "/api/scoped/move-to-project"
    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id: m.id, sourceScope: m.scope, sourceProjectRoot: root, targetProjectRoot: root }),
    })
    if (res.ok) {
      await load()
      scrollToTop()
    } else {
      setError(`Move failed: ${res.status}`)
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

  // The list arrives from the backend already filtered (type, query) and
  // ordered (recency); `matching` counts exactly what is in it.

  return (
    <div className="flex w-full flex-col">
      <DashboardHeader label="Memories">
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
            {matching} of {total}
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
      </DashboardHeader>

      <div className="flex min-h-0 w-full flex-1">
        <WorkspaceScopePanel
          rows={rows}
          selection={workspaceSelection}
          onSelectAll={() => applySelection(selectAllProjects())}
          onSelectOnly={(root) =>
            applySelection(selectWorkspaces([root], globalOn(selection)))
          }
          onToggleRoot={(root) =>
            applySelection(
              fromWorkspaceSelection(
                toggleRoot(workspaceSelection, root, allRoots),
                globalOn(selection),
              )
            )
          }
        />
        <div className="flex min-w-0 flex-1 flex-col gap-4 p-4 pt-4">
          {error && (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
              {error}
            </div>
          )}

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            {loading ? (
              Array.from({ length: 3 }).map((_, i) => (
                <Skeleton key={i} className="h-20 rounded-lg" />
              ))
            ) : (
              <>
                <div className="rounded-lg border p-3 text-sm">
                  <div className="text-muted-foreground">Total</div>
                  <div
                    className="text-xl font-medium"
                    title={`${total} in scope, ${matching} matching`}
                  >
                    {total}
                  </div>
                </div>
                <div className="rounded-lg border p-3 text-sm">
                  <div className="text-muted-foreground">Showing</div>
                  <div className="text-xl font-medium">{matching}</div>
                </div>
                <div className="rounded-lg border p-3 text-sm">
                  <div className="text-muted-foreground">Scope</div>
                  <div
                    className="truncate text-sm"
                    title={
                      selection.kind === "selection"
                        ? [
                            ...(selection.global ? ["🌐 Global Scope"] : []),
                            ...selection.roots,
                          ].join("\n")
                        : "All projects (aggregated)"
                    }
                  >
                    {scopeTileLabel(selection)}
                    {tierApplies(selection) && tier !== "project"
                      ? tier === "all"
                        ? " · 🗂 All tiers"
                        : " · 💻 Local"
                      : ""}
                  </div>
                </div>
              </>
            )}
          </div>

          <div className="truncate text-xs text-muted-foreground" title={rows.map((r) => r.root).join("\n")}>
            {rows.length} {rows.length === 1 ? "workspace" : "workspaces"} available
          </div>

          <div className="sticky top-16 z-10 flex flex-col gap-2 bg-background pb-2 pt-1">
            <div className="flex items-center gap-2">
              <Input
                placeholder="Search content or memory ID..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="min-w-0 flex-1"
              />
              {/* Narrow screens keep the dropdown; the panel covers wide ones. */}
              <div className="lg:hidden">
                <WorkspaceSelect
                  rows={rows}
                  selection={workspaceSelection}
                  onSelectAll={() => applySelection(selectAllProjects())}
                  onSelectOnly={(root) =>
                    applySelection(selectWorkspaces([root], globalOn(selection)))
                  }
                  label={
                    selection.kind === "selection" && selection.roots.length === 0
                      ? "🌐 Global scope"
                      : undefined
                  }
                />
              </div>
              <Button
                variant={globalOn(selection) ? "default" : "outline"}
                size="sm"
                onClick={() => {
                  if (selection.kind === "all") {
                    applySelection(selectGlobalScope())
                    return
                  }
                  if (selection.global) {
                    applySelection(
                      selection.roots.length === 0
                        ? selectAllProjects()
                        : selectWorkspaces(selection.roots, false),
                    )
                    return
                  }
                  applySelection(selectWorkspaces(selection.roots, true))
                }}
                className="shrink-0"
                aria-pressed={globalOn(selection)}
                title={
                  globalOn(selection)
                    ? "Global scope included — click to exclude it"
                    : "Also include the global scope in this list"
                }
              >
                <Globe className="size-4" />
                <span className="hidden sm:inline">Global</span>
              </Button>
            </div>

            <div className="flex flex-wrap items-center gap-2">

            {tierApplies(selection) && (
              <div
                className="flex shrink-0 items-center gap-1 rounded-lg border bg-muted/40 p-1"
                role="group"
                aria-label="Project tier"
              >
                {(["all", "project-local", "project"] as const).map((t) => (
                  <button
                    key={t}
                    onClick={() => handleTierChange(t)}
                    className={
                      (tier === t
                        ? "rounded-md bg-background px-3 py-1 text-xs font-medium text-foreground shadow-sm"
                        : "rounded-md px-3 py-1 text-xs font-medium text-muted-foreground hover:text-foreground") +
                      " whitespace-nowrap"
                    }
                  >
                    {t === "project-local" ? "💻 Local" : t === "all" ? "🗂 All" : "📁 Shared"}
                  </button>
                ))}
              </div>
            )}

            <div
              className="flex shrink-0 items-center gap-1 rounded-lg border bg-muted/40 p-1"
              role="group"
              aria-label="Memory type"
            >
              {TYPES.map((t) => (
                <button
                  key={t}
                  onClick={() => setType(t)}
                  className={
                    (type === t
                      ? "rounded-md bg-background px-2.5 py-1 text-xs font-medium text-foreground shadow-sm"
                      : "rounded-md px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground") +
                      " whitespace-nowrap"
                  }
                >
                  {t}
                </button>
              ))}
            </div>
            </div>
          </div>

          {loading ? (
            <div className="flex flex-col gap-2">
              {Array.from({ length: 5 }).map((_, i) => (
                <Skeleton key={i} className="h-20 w-full rounded-xl" />
              ))}
            </div>
          ) : memories.length === 0 ? (
            <div className="flex min-h-[200px] items-center justify-center rounded-xl border border-dashed text-sm text-muted-foreground">
              {total > 0
                ? "No memories match this filter."
                : "No memories found."}
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-2.5 lg:grid-cols-2">
              {memories.map((m) =>
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
                    onMoveTier={(target, tier) => void moveTier(target, tier)}
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
                  Original memory will be deleted from this project and moved permanently to Global scope.
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
                  Original memory stays in {copyTarget?.scope === "global" ? "Global" : "this project"}; a new copy is created
                  in the target project.
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
    </div>
  )
}
