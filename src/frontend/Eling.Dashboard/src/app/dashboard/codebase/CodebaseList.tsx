"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import { RefreshCw, Search, ChevronDown } from "lucide-react"
import { DashboardHeader } from "@/components/dashboard-header"
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
import { LoadingBar } from "@/components/ui/loading-bar"
import { Skeleton } from "@/components/ui/skeleton"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import type { SseTopic } from "@/hooks/use-memories-sse"
import {
  useCodebaseRebuildSse,
  type RebuildProgress,
} from "@/hooks/use-codebase-rebuild-sse"
import { useMediaQuery } from "@/hooks/use-media-query"
import { utcToLocal } from "@/lib/date-utils"
import { XL_BREAKPOINT } from "@/lib/constants"
import { cn } from "@/lib/utils"
import { FileDetailDialog } from "./FileDetailDialog"
import { FileDetailPanel } from "./FileDetailPanel"
import {
  FilePathLink,
  FileResultList,
  WorkspaceBadge,
  type FileResult,
} from "./FileResultList"
import type { SelectedFile } from "./fileDetailApi"
import { readQuery, rememberQuery } from "./searchPrefs"
import { SearchScopeToggle } from "./SearchScopeToggle"
import {
  rebuildUrl,
  queryFromParams,
  queryParam,
  searchScopeFromParams,
  searchScopeQuery,
  searchUrl,
  selectionFromParams,
  selectionQuery,
  statusUrl,
  SEARCH_SCOPE_COPY,
  type SearchScope,
} from "./scope"
import {
  selectAll,
  selectOnly,
  toggleRoots,
  type WorkspaceSelection,
} from "@/components/workspaces/selection"
import { useWorkspaceRows } from "@/components/workspaces/use-workspace-rows"
import { workspaceName } from "@/components/workspaces/runtime-rows"
import { WorkspaceScopePanel } from "@/components/workspaces/workspace-scope-panel"
import { WorkspaceSelect } from "@/components/workspaces/workspace-select"

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

type RebuildMode = "incremental" | "full"

function rebuildModeLabel(mode: RebuildMode) {
  return mode === "full" ? "Full re-index" : "Incremental"
}

// URL builders and this page's URL binding of the selection live in ./scope.ts;
// the selection model itself is shared in @/components/workspaces/selection.

// The open file's URL identity. Two params rather than one encoded pair: both
// values are paths, and unmerged they stay readable in the address bar and in
// a shared link. `line` jumps the viewer straight to a search hit's line.
function fileQuery(file: SelectedFile) {
  const parts = [
    `file=${encodeURIComponent(file.path)}`,
    `fileRoot=${encodeURIComponent(file.projectRoot)}`,
  ]
  if (file.startLine) parts.push(`line=${file.startLine}`)
  return parts.join("&")
}

function fileFromParams(params: URLSearchParams): SelectedFile | null {
  const path = params.get("file")?.trim()
  const root = params.get("fileRoot")?.trim()
  const line = params.get("line")?.trim()
  return path && root
    ? {
        projectRoot: root,
        path,
        ...(line ? { startLine: Number(line) } : {}),
      }
    : null
}

// Idle time after the last keystroke before a search is issued.
const SEARCH_DEBOUNCE_MS = 500

// While a status error is on screen the tiles are known-bad, so re-check the
// backend until it answers 200 again. The probe lives only in the error state
// and stops the instant loadStatus clears it (statusError → null), so healthy
// browsing never polls — the page keeps its "refresh only on events" design.
const HEALTH_PROBE_INTERVAL_MS = 5_000

export function CodebaseList() {
  const [statusLoading, setStatusLoading] = useState(true)
  const [searching, setSearching] = useState(false)
  const [status, setStatus] = useState<CodebaseStatus | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [results, setResults] = useState<CodebaseHit[]>([])
  const [fileResults, setFileResults] = useState<FileResult[]>([])
  const [searchError, setSearchError] = useState<string | null>(null)
  // The URL query is the selection's source of truth: no project params means
  // "all", otherwise the explicit root list, so reload, back/forward and shared
  // links all land on the same scope the tiles and rebuild describe.
  const searchParams = useSearchParams()
  const router = useRouter()
  const pathname = usePathname()
  const selection = useMemo(
    () => selectionFromParams(searchParams),
    [searchParams]
  )
  // The search scope is the second piece of URL state on this page. It
  // answers a different question: not which workspaces are read, but what
  // the query is matched against — the file contents, the file's path, or
  // the file's name.
  const searchScope = useMemo(
    () => searchScopeFromParams(searchParams),
    [searchParams]
  )
  // The query comes from the URL, and a bare address — a fresh visit, or the
  // sidebar link — falls back to the last query this browser ran, so the box
  // is not empty every time. Read during the first render, like the
  // selection and the scope: the page sits behind a Suspense boundary, so
  // this subtree is client-rendered and the server never produced a
  // different box to disagree with.
  const [query, setQuery] = useState(
    () => queryFromParams(searchParams) || readQuery()
  )
  // The query as it was when the last search went out. The URL carries this
  // one, not the raw text: a request only goes out once the reader pauses,
  // so the address bar should settle on the value that was actually
  // searched.
  const [issuedQuery, setIssuedQuery] = useState("")
  const { rows, error: runtimesError, reload } = useWorkspaceRows()
  const allRoots = useMemo(() => rows.map((r) => r.root), [rows])
  // The open file is URL state, same as the scope: reload, back/forward and
  // shared links all land on the same viewer state. `file` carries the path
  // and `fileRoot` the owning project — the same relative path can exist in
  // two indexed workspaces, and the viewer must know which one to read.
  const selectedFile = useMemo(
    () => fileFromParams(searchParams),
    [searchParams]
  )
  // One writer for all of this page's URL state, so changing one piece never
  // drops another: a scope change keeps the query and the open file, closing
  // the file keeps the scope, and a new query keeps the scope.
  const writeUrl = useCallback(
    (next: {
      selection: WorkspaceSelection
      scope: SearchScope
      query: string
      file: SelectedFile | null
    }) => {
      const parts = [
        selectionQuery(next.selection),
        searchScopeQuery(next.scope),
        queryParam(next.query),
        next.file ? fileQuery(next.file) : "",
      ].filter(Boolean)
      const qs = parts.join("&")
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [pathname, router]
  )
  // Selection changes flip the skeleton on right in the event handler — before
  // the router round-trip — so tiles react to the click, not to the next commit.
  const applySelection = useCallback(
    (next: WorkspaceSelection) => {
      setStatusLoading(true)
      writeUrl({
        selection: next,
        scope: searchScope,
        query,
        file: selectedFile,
      })
    },
    [writeUrl, searchScope, query, selectedFile]
  )
  const openFile = useCallback(
    (file: SelectedFile) =>
      writeUrl({ selection, scope: searchScope, query, file }),
    [writeUrl, selection, searchScope, query]
  )
  const closeFile = useCallback(
    () => writeUrl({ selection, scope: searchScope, query, file: null }),
    [writeUrl, selection, searchScope, query]
  )
  // Wide screens get the docked file panel; narrow ones keep the centered
  // dialog. Mirrors Tailwind's xl breakpoint, which FileDetailPanel's own
  // `xl:flex` class also encodes, so the JS gate and CSS agree by construction.
  const wide = useMediaQuery(`(min-width: ${XL_BREAKPOINT}px)`)
  const [topFiles, setTopFiles] = useState<FileResult[]>([])
  const [topLoading, setTopLoading] = useState(true)
  // With the panel docked, the list column is the fixed-width one and the
  // panel takes the rest — the list keeps its width whether the panel is open
  // or not, and the code viewer gets every remaining pixel.
  const panelOpen = selectedFile !== null && wide
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
  // The scope the results on screen belong to. A scope change is an explicit
  // action rather than a keystroke, so it searches at once instead of
  // waiting out the debounce a typed character gets.
  const shownScope = useRef(searchScope)
  // Last seen index timestamp — a change means someone rebuilt the index.
  const lastIndexedAtRef = useRef<string | null>(null)

  // All state writes land after the first await: this runs from an effect and
  // from SSE callbacks, and a synchronously-set loading flag there would
  // cascade an extra render commit per scope change.
  const loadStatus = useCallback(async () => {
    try {
      const res = await fetch(statusUrl(selection), { cache: "no-store" })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const s = (await res.json()) as CodebaseStatus
      setStatus(s)
      lastIndexedAtRef.current = s.lastIndexedAt
      setStatusError(null)
    } catch (e) {
      setStatusError(e instanceof Error ? e.message : "Failed to load status")
    } finally {
      setStatusLoading(false)
    }
  }, [selection])

  // Empty box shows the most recently indexed files instead of nothing.
  const loadTop = useCallback(async (silent: boolean) => {
    if (!silent) setTopLoading(true)
    try {
      const res = await fetch(searchUrl("", selection, searchScope), {
        cache: "no-store",
      })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const data = await res.json()
      setTopFiles(Array.isArray(data.results) ? data.results : [])
    } catch {
      if (!silent) setTopFiles([])
    } finally {
      if (!silent) setTopLoading(false)
    }
  }, [selection, searchScope])

  // Starts a rebuild over the selected scope/mode. The 202 body is already a
  // progress snapshot, so the UI has something to render before the first SSE
  // frame lands. Tiles refresh on each "codebase" notification from the
  // backend, and once more when the job reports it finished.
  const rebuild = useCallback(async () => {
    setRebuildStarting(true)
    setStatusError(null)
    try {
      const res = await fetch(
        rebuildUrl(selection, rebuildMode === "full", Date.now()),
        {
          method: "POST",
          cache: "no-store",
        }
      )
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      setRebuildProgress((await res.json()) as RebuildProgress)
    } catch (e) {
      setStatusError(e instanceof Error ? e.message : "Rebuild failed")
    } finally {
      setRebuildStarting(false)
    }
  }, [rebuildMode, selection])

  // A final snapshot also refetches the tiles, so the numbers stay correct
  // even if the SSE channel is down and the per-root notifications were lost.
  const handleRebuildProgress = useCallback(
    (progress: RebuildProgress) => {
      setRebuildProgress(progress)
      if (!progress.isRunning) void loadStatus()
    },
    [loadStatus]
  )

  // The hook stashes the callback in a ref, so passing the callback directly
  // does not resubscribe the stream when the scope changes.
  useCodebaseRebuildSse(handleRebuildProgress)

  // The scope travels with the query rather than being read from the
  // callback's closure: every request states which scope it is for, so a
  // response can never be filed under the wrong one.
  const doSearch = useCallback(async (q: string, scope: SearchScope, opts?: { silent?: boolean }) => {
    const trimmed = q.trim()
    const seq = ++searchSeq.current
    if (!trimmed) {
      setResults([])
      setFileResults([])
      setSearchError(null)
      setSearching(false)
      return
    }
    const silent = opts?.silent ?? false
    if (!silent) setSearching(true)
    setSearchError(null)
    try {
      const res = await fetch(searchUrl(trimmed, selection, scope), {
        cache: "no-store",
      })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const data = await res.json()
      if (searchSeq.current !== seq) return
      // Each scope has its own row shape and only one of them is ever on
      // screen, so they live in separate lists instead of one list the
      // renderer has to inspect.
      if (scope === "content") {
        setResults(data.results || [])
      } else {
        setFileResults(data.results || [])
      }
    } catch (e) {
      if (searchSeq.current !== seq) return
      if (!silent) {
        setResults([])
        setFileResults([])
        setSearchError(e instanceof Error ? e.message : "Search failed")
      }
    } finally {
      // Always clear on the latest sequence, even for silent refreshes:
      // a silent poll landing after a manual search started would otherwise
      // leave `searching` stuck true (skeletons + disabled button) forever,
      // because the superseded manual search skips its own reset above.
      if (searchSeq.current === seq) setSearching(false)
    }
  }, [selection])

  // Switching the scope drops the rows on screen and puts the new scope in
  // the URL; the search that follows belongs to the debounce effect, which
  // sees the changed scope. The rows go right away — they belong to the
  // scope that was just left, and showing them under the new scope would
  // read as if the search had been ignored.
  const applySearchScope = useCallback(
    (next: SearchScope) => {
      if (next === searchScope) return
      setResults([])
      setFileResults([])
      writeUrl({ selection, scope: next, query, file: selectedFile })
    },
    [searchScope, selection, query, selectedFile, writeUrl]
  )

  // Clearing the box resets the list immediately (in the event handler,
  // not in an effect, to avoid cascading renders).
  const handleQueryChange = useCallback(
    (value: string) => {
      setQuery(value)
      // Remembered on every keystroke, not on the debounce: the point is to
      // have the text back when the reader comes back, and coming back can
      // happen before the debounce ever fired.
      rememberQuery(value)
      if (!value.trim()) {
        if (debounceTimer.current) clearTimeout(debounceTimer.current)
        searchSeq.current++
        // The issued query goes with it, so the URL drops the `q` too —
        // otherwise a reload would bring the cleared query back.
        setIssuedQuery("")
        setResults([])
        setFileResults([])
        setSearchError(null)
        setSearching(false)
      }
    },
    []
  )

  // Tiles follow the selection: load on mount and on every scope change, so
  // the numbers, the result list and the rebuild target can never drift apart.
  // The awaited wrapper matches the repo's loader pattern (MemoriesList): every
  // state write in loadStatus lands after its first await.
  useEffect(() => {
    const run = async () => {
      await loadStatus()
    }
    void run()
  }, [loadStatus])

  // Reactive recovery: an error stays up only as long as the last health check
  // was bad. While either loader's error is set, keep probing; each loader
  // clears its own error on the first 200, which also tears this interval down
  // (self-stopping). The row reload runs silent so a probe never flashes the
  // banner or drops the last good rows while the failure persists.
  useEffect(() => {
    if (statusError === null && runtimesError === null) return
    const timer = setInterval(() => {
      void loadStatus()
      if (runtimesError !== null) void reload({ silent: true })
    }, HEALTH_PROBE_INTERVAL_MS)
    return () => clearInterval(timer)
  }, [statusError, runtimesError, loadStatus, reload])

  // Empty box shows the top-files listing; (re)load it on mount and
  // whenever the scope changes while the box stays empty.
  useEffect(() => {
    if (query.trim()) return
    const fetchTop = async () => {
      await loadTop(false)
    }
    void fetchTop()
  }, [query, loadTop])
  // Search-as-you-type: debounce keystrokes so a request goes out only
  // after the user pauses. A scope change skips the wait — the user asked
  // for something different, not for the same thing again.
  useEffect(() => {
    const scopeChanged = shownScope.current !== searchScope
    shownScope.current = searchScope
    if (!query.trim()) return
    debounceTimer.current = setTimeout(() => {
      setIssuedQuery(query)
      void doSearch(query, searchScope)
    }, scopeChanged ? 0 : SEARCH_DEBOUNCE_MS)
    return () => {
      if (debounceTimer.current) clearTimeout(debounceTimer.current)
    }
  }, [query, searchScope, doSearch])

  // The URL carries the text the last search used, so a reload or a shared
  // link brings the same query back. Skipped on the first run, so a `q` that
  // arrived in the URL is not clobbered before the reader types.
  const urlSyncReady = useRef(false)
  useEffect(() => {
    if (!urlSyncReady.current) {
      urlSyncReady.current = true
      return
    }
    if (queryFromParams(searchParams) === issuedQuery) return
    writeUrl({
      selection,
      scope: searchScope,
      query: issuedQuery,
      file: selectedFile,
    })
  }, [issuedQuery, searchParams, selection, searchScope, selectedFile, writeUrl])

  // Live refresh via SSE: the backend broadcasts "codebase" on every index
  // rebuild (button, MCP call, or background watcher, any process) and
  // "runtimes" on membership changes. No polling — refresh only on events.
  const handleCodebaseEvent = useCallback(async () => {
    try {
      const statusRes = await fetch(statusUrl(selection), { cache: "no-store" })
      if (!statusRes.ok) return
      const s = (await statusRes.json()) as CodebaseStatus
      setStatus(s)
      lastIndexedAtRef.current = s.lastIndexedAt
    } catch {
      // Transient failure: keep the last known status.
    }
    if (query.trim()) void doSearch(query, searchScope, { silent: true })
    else void loadTop(true)
  }, [selection, query, searchScope, doSearch, loadTop])

  const handleRuntimesEvent = useCallback(async () => {
    // Silent refresh: a failed background reload keeps the last known rows.
    await reload({ silent: true })
    // Membership changes alter the "all" aggregate, so refresh the numbers too.
    await handleCodebaseEvent()
  }, [reload, handleCodebaseEvent])

  // This page lives on codebase traffic: "codebase" for index changes anywhere
  // (rebuild button, MCP call, background watcher) and "runtimes" for membership
  // changes that alter both the picker and the "all" aggregate. It deliberately
  // does not watch the memory topics — those would reload the index on every
  // memory write.
  const CODEBASE_TOPICS: SseTopic[] = ["codebase", "runtimes"]
  useMemoriesSse(
    useCallback(() => void handleCodebaseEvent(), [handleCodebaseEvent]),
    useCallback(() => void handleRuntimesEvent(), [handleRuntimesEvent]),
    CODEBASE_TOPICS
  )

  // One banner for both loaders: the runtime-row fetch and the status fetch
  // describe the same backend failure, and the surfaces take turns being the
  // one that noticed first.
  const errorMessage = statusError ?? runtimesError

  const failedProjects = (rebuildProgress?.results ?? []).filter((r) => !r.ok)
  const hasRebuildReport =
    rebuildProgress !== null &&
    (rebuildProgress.total > 0 || rebuildProgress.skippedRoots.length > 0)

  // The workspace badge is only worth the space when the scope covers more
  // than one workspace — with a single one, every row would repeat it.
  const showWorkspace = selection.kind === "all" || selection.roots.length > 1

  // Top-files listing shown when the query box is empty, and also while the
  // first search is in flight (no previous results to keep visible yet).
  const topFilesView = (
    <>
      {topLoading && topFiles.length === 0 ? (
        <div className="p-4 text-sm text-muted-foreground">
          Loading recent files…
        </div>
      ) : topFiles.length ? (
        <FileResultList
          files={topFiles}
          showWorkspace={showWorkspace}
          onOpen={openFile}
        />
      ) : (
        <div className="p-4 text-sm text-muted-foreground">
          Index is empty. Rebuild to populate.
        </div>
      )}
    </>
  )

  // A content hit: the snippet that matched, and the line range it sits in.
  const hitRows = results.map((r: CodebaseHit, i: number) => (
    <div key={`${r.projectRoot}-${r.path}-${r.startLine}-${i}`} className="p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <FilePathLink file={r} onOpen={openFile} />
        {showWorkspace && <WorkspaceBadge root={r.projectRoot} />}
      </div>
      <div className="text-muted-foreground text-xs">
        line {r.startLine}–{r.endLine} · score {r.score?.toFixed(2)}
      </div>
      <div className="mt-1 whitespace-pre-wrap break-all line-clamp-6 text-xs text-foreground/80">
        {r.content}
      </div>
    </div>
  ))

  // A content search with no hits falls back to the recent-files listing —
  // that is how the page has always answered. A file search does not: the
  // answer is "no file matches this", and unrelated files under that heading
  // would read as if the search had been ignored.
  const noFileMatch = (
    <div className="p-4 text-sm text-muted-foreground">
      No file matches “{query.trim()}”.
    </div>
  )

  const contentView = results.length > 0 ? hitRows : topFilesView
  const fileView =
    fileResults.length > 0 ? (
      <FileResultList
        files={fileResults}
        showWorkspace={showWorkspace}
        onOpen={openFile}
      />
    ) : (
      noFileMatch
    )
  const resultsView = searchScope === "content" ? contentView : fileView
  return (
    <div className="flex w-full flex-col">
      <DashboardHeader label="Codebase">
          <Button
            variant="outline"
            size="sm"
            onClick={() => void loadStatus()}
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
      </DashboardHeader>

      <div className="flex min-h-0 w-full flex-1">
        <WorkspaceScopePanel
          rows={rows}
          selection={selection}
          onSelectAll={() => applySelection(selectAll())}
          onSelectOnly={(root) => applySelection(selectOnly(root))}
          onToggleRoots={(roots) =>
            applySelection(toggleRoots(selection, roots, allRoots))
          }
        />
        <div
          className={cn(
            "flex min-w-0 flex-col gap-4 p-4 pt-4",
            // Closed: the list column fills the row, like it always did. Open:
            // it locks to 30rem and stops growing, so the docked panel takes
            // every remaining pixel instead of the two sharing the space.
            panelOpen ? "shrink-0 xl:w-[30rem]" : "flex-1"
          )}
        >
        {errorMessage && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {errorMessage}
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
                Indexing {workspaceName(rebuildProgress.currentProject)}
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
                    {workspaceName(r.projectRoot)}: {r.error}
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

        <div
          className={cn(
            "grid grid-cols-1 gap-3",
            // Closed, the column spans the page and equal thirds fill it
            // generously. Open, the column is locked at 36rem and even thirds
            // squeeze the tiles — a 7-column grid with 2:2:3 spans gives Files
            // and Chunks room for their numbers and Last indexed the extra
            // width a full timestamp needs.
            panelOpen ? "sm:grid-cols-7" : "sm:grid-cols-3"
          )}
        >
          {statusLoading ? (
            // Same conditional spans as the real tiles below: in the 7-column
            // open layout, three unspanned skeletons would fill only the first
            // three columns and leave the row ragged.
            Array.from({ length: 3 }).map((_, i) => (
              <Skeleton
                key={i}
                className={cn(
                  "h-20 rounded-lg",
                  panelOpen && (i === 2 ? "sm:col-span-3" : "sm:col-span-2")
                )}
              />
            ))
          ) : status ? (
            <>
              <div
                className={cn(
                  "rounded-lg border p-3 text-sm",
                  panelOpen && "sm:col-span-2"
                )}
              >
                <div className="text-muted-foreground">Files</div>
                <div className="text-xl font-medium">{status.files}</div>
              </div>
              <div
                className={cn(
                  "rounded-lg border p-3 text-sm",
                  panelOpen && "sm:col-span-2"
                )}
              >
                <div className="text-muted-foreground">Chunks</div>
                <div className="text-xl font-medium">{status.chunks}</div>
              </div>
              <div
                className={cn(
                  "rounded-lg border p-3 text-sm",
                  panelOpen && "sm:col-span-3"
                )}
              >
                <div className="text-muted-foreground">Last indexed</div>
                <div className="truncate text-sm" title={status.lastIndexedAt ?? ""}>
                  {utcToLocal(status.lastIndexedAt) || "—"}
                </div>
              </div>
            </>
          ) : (
            <div
              className={cn(
                "rounded-lg border border-dashed p-4 text-sm text-muted-foreground",
                panelOpen ? "sm:col-span-7" : "sm:col-span-3"
              )}
            >
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
            : `Project: ${workspaceName((status?.roots ?? [])[0])}`}{" "}
          · watcher {status?.watcherActive ? "on" : "off"}
        </div>

        <form
          className="sticky top-16 z-10 flex flex-wrap gap-2 bg-background py-1"
          onSubmit={(e) => {
            e.preventDefault()
            // Enter searches immediately instead of waiting for the debounce.
            if (debounceTimer.current) clearTimeout(debounceTimer.current)
            void doSearch(query, searchScope)
          }}
        >
          <SearchScopeToggle
            scope={searchScope}
            onChange={applySearchScope}
          />
          <Input
            placeholder={SEARCH_SCOPE_COPY[searchScope].placeholder}
            aria-label="Codebase search query"
            value={query}
            onChange={(e) => handleQueryChange(e.target.value)}
            className="min-w-0 flex-1"
          />
          {/* Narrow screens keep the dropdown; the panel covers wide ones. */}
          <div className="lg:hidden">
            <WorkspaceSelect
              rows={rows}
              selection={selection}
              onSelectAll={() => applySelection(selectAll())}
              onSelectOnly={(root) => applySelection(selectOnly(root))}
              onToggleRoots={(roots) =>
                applySelection(toggleRoots(selection, roots, allRoots))
              }
            />
          </div>
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
          {SEARCH_SCOPE_COPY[searchScope].hint}
        </p>

        {searchError && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {searchError}
          </div>
        )}

        <div className="relative min-w-0">
          <LoadingBar loading={searching} />
          <div className="min-w-0 rounded-lg border divide-y">
          {!query.trim() ? (
            topFilesView
          ) : (
            resultsView
          )}
        </div>
      </div>
      </div>
      {/* Wide screens dock the viewer as this row's right-hand column; narrow
          ones keep the centered modal. The same selectedFile drives both, so
          resizing across the breakpoint swaps only the chrome, never the
          content. */}
      {panelOpen && (
        <FileDetailPanel file={selectedFile} onClose={closeFile} />
      )}
      </div>

      {!wide && (
        <FileDetailDialog file={selectedFile} onClose={closeFile} />
      )}
    </div>
  )
}
