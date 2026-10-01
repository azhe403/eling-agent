"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { RefreshCw, Search, BookOpen, ChevronDown } from "lucide-react"
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbList,
  BreadcrumbPage,
} from "@/components/ui/breadcrumb"
import { Button } from "@/components/ui/button"
import { ButtonGroup } from "@/components/ui/button-group"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import { SidebarTrigger } from "@/components/ui/sidebar"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import {
  useCodebaseRebuildSse,
  type RebuildProgress,
} from "@/hooks/use-codebase-rebuild-sse"
import { utcToLocal } from "@/lib/date-utils"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { FileDetailDialog } from "./FileDetailDialog"
import type { SelectedFile } from "./fileDetailApi"

type CodebaseStatus = {
  scope: string
  projectCount: number
  roots: string[]
  files: number
  chunks: number
  lastIndexedAt: string | null
  watcherActive: boolean
  dbPath: string
}

type CodebaseHit = {
  projectRoot: string
  path: string
  startLine: number
  endLine: number
  content: string
  score: number
}

type RuntimeEntry = {
  projectRoot: string
  workspaceRoot?: string | null
  codebaseEnabled?: boolean | null
}

type CodebaseTopFile = {
  projectRoot: string
  path: string
  chunkCount: number
  lastIndexedAt: string
}

/** A file name in either list, rendered as the button that opens the viewer. */
function FileName({
  file,
  onOpen,
}: {
  file: { projectRoot: string; path: string }
  onOpen: (file: SelectedFile) => void
}) {
  return (
    <button
      type="button"
      onClick={() => onOpen(file)}
      title={`Open ${file.path}`}
      className="text-left font-medium break-all underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-xs"
    >
      {file.path}
    </button>
  )
}

type RebuildMode = "incremental" | "full"

function rebuildModeLabel(mode: RebuildMode) {
  return mode === "full" ? "Full re-index" : "Incremental"
}

function searchUrl(q: string, sel: string) {
  const base = `/api/codebase/search?limit=20`
  const scopePart = sel === "all" ? "&scope=all" : `&project=${encodeURIComponent(sel)}`
  return q.trim() ? `${base}&q=${encodeURIComponent(q.trim())}${scopePart}` : `${base}${scopePart}`
}

// Same scope selector as search, so the tiles always describe the same set of
// project indexes the result list is reading from. An empty selection means
// "this backend's own project" — the endpoint default.
function statusUrl(sel: string) {
  if (!sel) return `/api/codebase/status`
  return sel === "all"
    ? `/api/codebase/status?scope=all`
    : `/api/codebase/status?project=${encodeURIComponent(sel)}`
}

// Rebuild honours the same scope selector as search, so the projects that get
// indexed are the ones the tiles and result list are describing. An empty
// selection means "this backend's own project" — the endpoint default.
function rebuildUrl(sel: string, mode: RebuildMode) {
  const scopePart = !sel
    ? ""
    : sel === "all"
      ? "&scope=all"
      : `&project=${encodeURIComponent(sel)}`
  return `/api/codebase/rebuild-index?full=${mode === "full"}&_t=${Date.now()}${scopePart}`
}

function distinctRoots(runtimes: RuntimeEntry[]) {
  const out: string[] = []
  const seen = new Set<string>()
  const add = (root: string | undefined | null) => {
    if (!root || root === "UserScope") return
    const key = root.toLowerCase()
    if (seen.has(key)) return
    seen.add(key)
    out.push(root)
  }
  // Codebase identity is the workspace (cwd), not the memory project root —
  // fall back to projectRoot for runtimes registered by older binaries.
  // Runtimes that opted out of indexing never appear as options.
  for (const r of runtimes) {
    if (r.codebaseEnabled === false) continue
    add(r.workspaceRoot || r.projectRoot)
  }
  return out
}

function projectName(root: string | undefined | null) {
  if (!root) return "—"
  const parts = root.split(/[/\\]+/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : root
}

// Idle time after the last keystroke before a search is issued.
const SEARCH_DEBOUNCE_MS = 500

export function CodebaseList() {
  const [statusLoading, setStatusLoading] = useState(true)
  const [searching, setSearching] = useState(false)
  const [status, setStatus] = useState<CodebaseStatus | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [results, setResults] = useState<CodebaseHit[]>([])
  const [searchError, setSearchError] = useState<string | null>(null)
  const [query, setQuery] = useState("")
  // "all" = every alive project, otherwise an explicit project root.
  // Defaults to "all" so the first paint already shows every index.
  const [projectSel, setProjectSel] = useState<string>("all")
  const handleScopeChange = useCallback((value: string | null) => {
    if (!value) return
    setProjectSel(value)
  }, [])
  const [runtimes, setRuntimes] = useState<RuntimeEntry[]>([])
  const [topFiles, setTopFiles] = useState<CodebaseTopFile[]>([])
  const [topLoading, setTopLoading] = useState(true)
  // Which file the viewer is showing, or null when it is closed. Both lists
  // feed the same viewer, so the selection carries the owning project with it:
  // the same relative path can exist in two indexed workspaces.
  const [selectedFile, setSelectedFile] = useState<SelectedFile | null>(null)
  // Rebuild is an async job: the POST returns 202 with the first snapshot and
  // the rest arrives over SSE. `rebuildStarting` covers only the POST itself,
  // before any snapshot exists.
  const [rebuildMode, setRebuildMode] = useState<RebuildMode>("incremental")
  const [rebuildProgress, setRebuildProgress] = useState<RebuildProgress | null>(null)
  const [rebuildStarting, setRebuildStarting] = useState(false)
  const rebuilding =
    rebuildStarting || (rebuildProgress?.isRunning ?? false)

  // Monotonic id: only the latest issued search may write results,
  // so a slow earlier response can never overwrite a newer one.
  const searchSeq = useRef(0)
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  // Last seen index timestamp — a change means someone rebuilt the index.
  const lastIndexedAtRef = useRef<string | null>(null)

  const loadStatus = useCallback(async (sel: string) => {
    setStatusLoading(true)
    setStatusError(null)
    try {
      const res = await fetch(statusUrl(sel), { cache: "no-store" })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const s = (await res.json()) as CodebaseStatus
      setStatus(s)
      lastIndexedAtRef.current = s.lastIndexedAt
    } catch (e) {
      setStatusError(e instanceof Error ? e.message : "Failed to load status")
    } finally {
      setStatusLoading(false)
    }
  }, [])

  // Empty box shows the most recently indexed files instead of nothing.
  const loadTop = useCallback(async (silent: boolean) => {
    if (!silent) setTopLoading(true)
    try {
      const res = await fetch(searchUrl("", projectSel), { cache: "no-store" })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const data = await res.json()
      setTopFiles(Array.isArray(data.results) ? data.results : [])
    } catch {
      if (!silent) setTopFiles([])
    } finally {
      if (!silent) setTopLoading(false)
    }
  }, [projectSel])

  // Fetch status + known projects on mount so the page is alive immediately.
  useEffect(() => {
    let isMounted = true
    const fetchOnMount = async () => {
      setStatusLoading(true)
      setStatusError(null)
      try {
        const [statusRes, runtimesRes] = await Promise.all([
          fetch(statusUrl(""), { cache: "no-store" }),
          fetch("/api/coordinator/runtimes", { cache: "no-store" }),
        ])
        if (!statusRes.ok) throw new Error(`API returned ${statusRes.status}`)
        const s = (await statusRes.json()) as CodebaseStatus
        if (!isMounted) return
        setStatus(s)
        lastIndexedAtRef.current = s.lastIndexedAt
        if (runtimesRes.ok) {
          const r = (await runtimesRes.json()) as RuntimeEntry[]
          if (isMounted && Array.isArray(r)) setRuntimes(r)
        }
      } catch (e) {
        if (isMounted)
          setStatusError(e instanceof Error ? e.message : "Failed to load status")
      } finally {
        if (isMounted) setStatusLoading(false)
      }
    }
    void fetchOnMount()
    return () => {
      isMounted = false
    }
  }, [])

  // Starts a rebuild over the selected scope/mode. The 202 body is already a
  // progress snapshot, so the UI has something to render before the first SSE
  // frame lands. Tiles refresh on each "codebase" notification from the
  // backend, and once more when the job reports it finished.
  const rebuild = useCallback(async () => {
    setRebuildStarting(true)
    setStatusError(null)
    try {
      const res = await fetch(rebuildUrl(projectSel, rebuildMode), {
        method: "POST",
        cache: "no-store",
      })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      setRebuildProgress((await res.json()) as RebuildProgress)
    } catch (e) {
      setStatusError(e instanceof Error ? e.message : "Rebuild failed")
    } finally {
      setRebuildStarting(false)
    }
  }, [projectSel, rebuildMode])

  // A final snapshot also refetches the tiles, so the numbers stay correct
  // even if the SSE channel is down and the per-root notifications were lost.
  const handleRebuildProgress = useCallback(
    (progress: RebuildProgress) => {
      setRebuildProgress(progress)
      if (!progress.isRunning) void loadStatus(projectSel)
    },
    [loadStatus, projectSel]
  )

  // The hook stashes the callback in a ref, so passing the callback directly
  // does not resubscribe the stream when `projectSel` changes.
  useCodebaseRebuildSse(handleRebuildProgress)

  const doSearch = useCallback(async (q: string, opts?: { silent?: boolean }) => {
    const trimmed = q.trim()
    const seq = ++searchSeq.current
    if (!trimmed) {
      setResults([])
      setSearchError(null)
      setSearching(false)
      return
    }
    const silent = opts?.silent ?? false
    if (!silent) setSearching(true)
    setSearchError(null)
    try {
      const res = await fetch(searchUrl(trimmed, projectSel), { cache: "no-store" })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const data = await res.json()
      if (searchSeq.current !== seq) return
      setResults(data.results || [])
    } catch (e) {
      if (searchSeq.current !== seq) return
      if (!silent) {
        setResults([])
        setSearchError(e instanceof Error ? e.message : "Search failed")
      }
    } finally {
      // Always clear on the latest sequence, even for silent refreshes:
      // a silent poll landing after a manual search started would otherwise
      // leave `searching` stuck true (skeletons + disabled button) forever,
      // because the superseded manual search skips its own reset above.
      if (searchSeq.current === seq) setSearching(false)
    }
  }, [projectSel])

  // Clearing the box resets the list immediately (in the event handler,
  // not in an effect, to avoid cascading renders).
  const handleQueryChange = useCallback(
    (value: string) => {
      setQuery(value)
      if (!value.trim()) {
        if (debounceTimer.current) clearTimeout(debounceTimer.current)
        searchSeq.current++
        setResults([])
        setSearchError(null)
        setSearching(false)
      }
    },
    []
  )

  // Scope change: the tiles must describe the same project indexes the result
  // list reads, so refetch status for the new selection. The first run is
  // skipped because the mount effect already loaded this backend's own status.
  const statusScopeMounted = useRef(false)
  useEffect(() => {
    if (!statusScopeMounted.current) {
      statusScopeMounted.current = true
      return
    }
    void loadStatus(projectSel)
  }, [projectSel, loadStatus])

  // Empty box shows the top-files listing; (re)load it on mount and
  // whenever the scope changes while the box stays empty.
  useEffect(() => {
    if (query.trim()) return
    const fetchTop = async () => {
      await loadTop(false)
    }
    void fetchTop()
  }, [query, projectSel, loadTop])
  // Search-as-you-type: debounce keystrokes so a request goes out only
  // after the user pauses.
  useEffect(() => {
    if (!query.trim()) return
    debounceTimer.current = setTimeout(() => {
      void doSearch(query)
    }, SEARCH_DEBOUNCE_MS)
    return () => {
      if (debounceTimer.current) clearTimeout(debounceTimer.current)
    }
  }, [query, doSearch])

  // Live refresh via SSE: the backend broadcasts "codebase" on every index
  // rebuild (button, MCP call, or background watcher, any process) and
  // "runtimes" on membership changes. No polling — refresh only on events.
  const handleCodebaseEvent = useCallback(async () => {
    try {
      const statusRes = await fetch(statusUrl(projectSel), { cache: "no-store" })
      if (!statusRes.ok) return
      const s = (await statusRes.json()) as CodebaseStatus
      setStatus(s)
      lastIndexedAtRef.current = s.lastIndexedAt
    } catch {
      // Transient failure: keep the last known status.
    }
    if (query.trim()) void doSearch(query, { silent: true })
    else void loadTop(true)
  }, [projectSel, query, doSearch, loadTop])

  const handleRuntimesEvent = useCallback(async () => {
    try {
      const runtimesRes = await fetch("/api/coordinator/runtimes", { cache: "no-store" })
      if (runtimesRes.ok) {
        const r = (await runtimesRes.json()) as RuntimeEntry[]
        if (Array.isArray(r)) setRuntimes(r)
      }
    } catch {
      // Transient failure: keep the last known runtimes.
    }
    // Membership changes alter the "all" aggregate, so refresh the numbers too.
    await handleCodebaseEvent()
  }, [handleCodebaseEvent])

  useMemoriesSse(
    useCallback(() => void handleCodebaseEvent(), [handleCodebaseEvent]),
    useCallback(() => void handleRuntimesEvent(), [handleRuntimesEvent])
  )

  const failedProjects = (rebuildProgress?.results ?? []).filter((r) => !r.ok)
  const hasRebuildReport =
    rebuildProgress !== null &&
    (rebuildProgress.total > 0 || rebuildProgress.skippedRoots.length > 0)

  return (
    <div className="flex h-svh flex-col overflow-hidden">
      <header className="flex h-16 shrink-0 items-center justify-between gap-2 border-b border-border/40 px-4">
        <div className="flex items-center gap-2">
          <SidebarTrigger className="-ml-1" />
          <Separator
            orientation="vertical"
            className="mr-2 data-vertical:h-4 data-vertical:self-auto"
          />
          <Breadcrumb>
            <BreadcrumbList>
              <BreadcrumbItem>
                <BreadcrumbPage className="flex items-center gap-2">
                  <BookOpen className="size-4 text-muted-foreground" />
                  Codebase Index
                </BreadcrumbPage>
              </BreadcrumbItem>
            </BreadcrumbList>
          </Breadcrumb>
        </div>

        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => void loadStatus(projectSel)}
            disabled={statusLoading}
          >
            <RefreshCw
              className={statusLoading ? "size-4 animate-spin" : "size-4"}
            />
            <span className="hidden sm:inline-block">Status</span>
          </Button>
          <ButtonGroup>
            <Button
              size="sm"
              disabled={rebuilding}
              onClick={() => void rebuild()}
              title={`Rebuild mode: ${rebuildModeLabel(rebuildMode)}`}
              aria-label={`Rebuild index, ${rebuildModeLabel(rebuildMode)} mode`}
            >              <RefreshCw
                className={rebuilding ? "size-4 animate-spin" : "size-4"}
              />
              <span className="hidden sm:inline-block">
                {rebuilding ? "Indexing…" : "Rebuild"}
              </span>
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger
                render={<Button variant="outline" size="icon-sm" />}
              >
                <ChevronDown className="size-4" />
                <span className="sr-only">Rebuild mode</span>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="w-44">
                {/* The label must sit inside a Menu.Group context — Base
                    MenuGroupLabel calls setLabelId from that context in a
                    layout effect, and crashes when it is missing. The
                    RadioGroup provides it, and picking it up also wires the
                    group's aria-labelledby. */}
                <DropdownMenuRadioGroup
                  value={rebuildMode}
                  onValueChange={(value) =>
                    setRebuildMode(value as RebuildMode)
                  }
                >
                  <DropdownMenuLabel>Rebuild mode</DropdownMenuLabel>
                  <DropdownMenuRadioItem value="incremental">
                    Incremental
                  </DropdownMenuRadioItem>
                  <DropdownMenuRadioItem value="full">
                    Full re-index
                  </DropdownMenuRadioItem>
                </DropdownMenuRadioGroup>
              </DropdownMenuContent>
            </DropdownMenu>
          </ButtonGroup>
        </div>
      </header>

      <div className="flex min-w-0 min-h-0 flex-1 flex-col gap-4 overflow-hidden p-4 pt-4">
        {statusError && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {statusError}
          </div>
        )}

        {rebuildProgress && hasRebuildReport && (
          <div className="rounded-lg border p-3 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <RefreshCw
                className={
                  rebuildProgress.isRunning
                    ? "size-4 animate-spin text-muted-foreground"
                    : "size-4 text-muted-foreground"
                }
              />
              <span className="font-medium">
                {rebuildProgress.isRunning
                  ? "Rebuilding…"
                  : "Rebuild finished"}
              </span>
              <span className="text-muted-foreground">
                {rebuildProgress.done}/{rebuildProgress.total}{" "}
                {rebuildProgress.total === 1 ? "project" : "projects"} ·{" "}
                {rebuildProgress.full ? "full re-index" : "incremental"} ·{" "}
                {rebuildProgress.scope}
              </span>
            </div>

            {rebuildProgress.currentProject && (
              <div className="mt-1 truncate text-xs text-muted-foreground">
                Indexing {projectName(rebuildProgress.currentProject)}
              </div>
            )}

            {rebuildProgress.error && (
              <div className="mt-1 text-xs text-destructive">
                Job aborted: {rebuildProgress.error}
              </div>
            )}

            {failedProjects.length > 0 && (
              <ul className="mt-2 space-y-1 text-xs text-destructive">
                {failedProjects.map((r) => (
                  <li key={r.projectRoot} className="break-all">
                    {projectName(r.projectRoot)}: {r.error}
                  </li>
                ))}
              </ul>
            )}

            {rebuildProgress.skippedRoots.length > 0 && (
              <div className="mt-1 text-xs text-muted-foreground">
                {rebuildProgress.skippedRoots.length}{" "}
                {rebuildProgress.skippedRoots.length === 1
                  ? "project has"
                  : "projects have"}{" "}
                no index yet and {rebuildProgress.skippedRoots.length === 1 ? "was" : "were"}{" "}
                skipped
              </div>
            )}
          </div>
        )}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          {statusLoading ? (
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-20 rounded-lg" />
            ))
          ) : status ? (
            <>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Files</div>
                <div className="text-xl font-medium">{status.files}</div>
              </div>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Chunks</div>
                <div className="text-xl font-medium">{status.chunks}</div>
              </div>
              <div className="rounded-lg border p-3 text-sm">
                <div className="text-muted-foreground">Last indexed</div>
                <div className="truncate text-sm" title={status.lastIndexedAt ?? ""}>
                  {utcToLocal(status.lastIndexedAt) || "—"}
                </div>
              </div>
            </>
          ) : (
            <div className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground sm:col-span-3">
              No status available.
            </div>
          )}
        </div>

        <div
          className="truncate text-xs text-muted-foreground"
          title={
            status && status.scope !== "project"
              ? (status.roots ?? []).join("\n")
              : ((status?.roots ?? [])[0] ?? "")
          }
        >
          {status && status.scope !== "project"
            ? `Tiles cover ${status.projectCount} ${status.projectCount === 1 ? "project" : "projects"} in scope`
            : `Project: ${projectName((status?.roots ?? [])[0])}`}{" "}
          · watcher {status?.watcherActive ? "on" : "off"}
        </div>

        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            // Enter searches immediately instead of waiting for the debounce.
            if (debounceTimer.current) clearTimeout(debounceTimer.current)
            void doSearch(query)
          }}
        >
          <Input
            placeholder="Type to search…"
            aria-label="Codebase search query"
            value={query}
            onChange={(e) => handleQueryChange(e.target.value)}
            className="min-w-0"
          />
          <Select value={projectSel} onValueChange={handleScopeChange}>
            <SelectTrigger
              aria-label="Search scope"
              title={projectSel}
              className="w-full max-w-44 shrink-0 truncate sm:max-w-72"
            >
              <SelectValue placeholder="Select scope...">
                {(v: string | null) =>
                  v === "all" ? "🌐 All projects" : `📁 ${projectName(v)}`
                }
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
              {distinctRoots(runtimes).map((root) => (
                <SelectItem key={root} value={root} title={root}>
                  <span className="flex min-w-0 flex-col items-start">
                    <span>📁 {projectName(root)}</span>
                    <span className="text-xs whitespace-normal break-all text-muted-foreground">
                      {root}
                    </span>
                  </span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            type="submit"
            size="icon"
            aria-label="Search"
            disabled={searching || !query.trim()}
          >
            <Search className={searching ? "size-4 animate-pulse" : "size-4"} />
          </Button>
        </form>
        <p className="-mt-2 text-xs text-muted-foreground">
          Empty box shows recently indexed files · search runs as you type ·
          list refreshes live when any process rebuilds the index.
        </p>

        {searchError && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {searchError}
          </div>
        )}

        <div className="min-w-0 min-h-0 flex-1 overflow-y-auto rounded-lg border divide-y">
          {!query.trim() ? (
            topLoading && topFiles.length === 0 ? (
              <div className="flex flex-col gap-2 p-3">
                {Array.from({ length: 3 }).map((_, i) => (
                  <Skeleton key={i} className="h-12 w-full rounded-lg" />
                ))}
              </div>
            ) : topFiles.length ? (
              topFiles.map((f) => (
                <div key={`${f.projectRoot}-${f.path}`} className="p-3 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <FileName file={f} onOpen={setSelectedFile} />
                    {projectSel === "all" && (
                      <span
                        className="rounded-full border border-border/50 bg-muted/50 px-2 py-0.5 text-[11px] font-medium text-muted-foreground"
                        title={f.projectRoot}
                      >
                        📁 {projectName(f.projectRoot)}
                      </span>
                    )}
                  </div>
                  <div className="text-muted-foreground text-xs">
                    {f.chunkCount} {f.chunkCount === 1 ? "chunk" : "chunks"} · indexed {utcToLocal(f.lastIndexedAt)}
                  </div>
                </div>
              ))
            ) : (
              <div className="p-4 text-sm text-muted-foreground">
                Index is empty. Rebuild to populate.
              </div>
            )
          ) : searching ? (
            <div className="flex flex-col gap-2 p-3">
              {Array.from({ length: 3 }).map((_, i) => (
                <Skeleton key={i} className="h-16 w-full rounded-lg" />
              ))}
            </div>
          ) : results.length ? (
            results.map((r: CodebaseHit, i: number) => (
              <div key={`${r.projectRoot}-${r.path}-${r.startLine}-${i}`} className="p-3 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <FileName file={r} onOpen={setSelectedFile} />
                  {projectSel === "all" && (
                    <span
                      className="rounded-full border border-border/50 bg-muted/50 px-2 py-0.5 text-[11px] font-medium text-muted-foreground"
                      title={r.projectRoot}
                    >
                      📁 {projectName(r.projectRoot)}
                    </span>
                  )}
                </div>
                <div className="text-muted-foreground text-xs">
                  line {r.startLine}–{r.endLine} · score {r.score?.toFixed(2)}
                </div>
                <div className="mt-1 whitespace-pre-wrap break-all line-clamp-6 text-xs text-foreground/80">
                  {r.content}
                </div>
              </div>
            ))
          ) : (
            <div className="p-4 text-sm text-muted-foreground">
              No results for this query.
            </div>
          )}
        </div>
      </div>

      <FileDetailDialog
        file={selectedFile}
        onClose={() => setSelectedFile(null)}
      />
    </div>
  )
}
