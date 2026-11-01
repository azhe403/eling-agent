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
import { rememberQuery } from "./searchPrefs"
import { SearchScopeToggle } from "./SearchScopeToggle"
import { SearchStatsRibbon } from "./SearchStatsRibbon"
import { SearchTabsBar } from "./SearchTabsBar"
import {
  createTab,
  closeTab,
  updateTab,
  loadTabs,
  saveTabs,
  type SearchTab,
  type SearchStats,
} from "./searchTabs"
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
  tabFromParams,
  tabParam,
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
  const searchParams = useSearchParams()
  const router = useRouter()
  const pathname = usePathname()

  const urlTabId = useMemo(() => tabFromParams(searchParams), [searchParams])
  const [tabsState, setTabsState] = useState(() => {
    const urlSelection = selectionFromParams(searchParams)
    const urlScope = searchScopeFromParams(searchParams)
    const urlQuery = queryFromParams(searchParams)
    const loaded = loadTabs(urlQuery, urlScope, urlSelection)
    if (urlTabId && loaded.tabs.some((t) => t.id === urlTabId)) {
      return { tabs: loaded.tabs, activeId: urlTabId }
    }
    if (urlTabId) {
      const newTab = createTab({
        id: urlTabId,
        query: urlQuery,
        scope: urlScope,
        selection: urlSelection,
      })
      return { tabs: [...loaded.tabs, newTab], activeId: newTab.id }
    }
    return loaded
  })
  const { tabs, activeId: activeTabId } = tabsState
  const activeTab = useMemo(
    () => tabs.find((t) => t.id === activeTabId) ?? tabs[0],
    [tabs, activeTabId]
  )
  const selection = activeTab.selection
  const searchScope = activeTab.scope

  // The query comes from the active tab.
  const [query, setQuery] = useState(() => activeTab.query)

  // Persist tab state whenever tabs or active tab changes, kept out of state updaters.
  useEffect(() => {
    saveTabs(tabsState.tabs, tabsState.activeId)
  }, [tabsState])

  // Tracks in-flight tab navigation initiated by user interaction to prevent
  // the URL back/forward sync effect from reverting back to the old URL tab id
  // before router.replace finishes updating searchParams.
  const pendingNavTabId = useRef<string | null>(null)

  // The query as it was when the last search went out.
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
      tab?: string
      selection: WorkspaceSelection
      scope: SearchScope
      query: string
      file: SelectedFile | null
    }) => {
      const tabId = next.tab ?? activeTab.id
      const parts = [
        tabParam(tabId),
        selectionQuery(next.selection),
        searchScopeQuery(next.scope),
        queryParam(next.query),
        next.file ? fileQuery(next.file) : "",
      ].filter(Boolean)
      const qs = parts.join("&")
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [pathname, router, activeTab.id]
  )
  // Selection changes flip the skeleton on right in the event handler — before
  // the router round-trip — so tiles react to the click, not to the next commit.
  const applySelection = useCallback(
    (next: WorkspaceSelection) => {
      setStatusLoading(true)
      setTabsState((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, { ...current, selection: next }),
        }
      })
      writeUrl({
        tab: activeTab.id,
        selection: next,
        scope: searchScope,
        query,
        file: selectedFile,
      })
    },
    [writeUrl, searchScope, query, selectedFile, activeTab.id]
  )
  const openFile = useCallback(
    (file: SelectedFile) =>
      writeUrl({ tab: activeTab.id, selection, scope: searchScope, query, file }),
    [writeUrl, activeTab.id, selection, searchScope, query]
  )
  const closeFile = useCallback(
    () => writeUrl({ tab: activeTab.id, selection, scope: searchScope, query, file: null }),
    [writeUrl, activeTab.id, selection, searchScope, query]
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
  // Persist the running job id so a page refresh can re-attach to an in-flight
  // rebuild instead of silently dropping the progress indicator.
  const REBUILD_JOB_KEY = "codebase:rebuildJobId"
  // The rebuild-mode dropdown is a base-ui Menu whose radio items do not
  // close the popup on selection (radio items are toggles, not actions).
  // Without this the popup stays open after picking a mode, which reads as
  // a checkbox. Controlling `open` and closing on `onValueChange` makes a
  // selection act like a normal menu item: pick, close, done.
  const [rebuildModeMenuOpen, setRebuildModeMenuOpen] = useState(false)
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
  const rebuild = useCallback(
    async (mode: RebuildMode) => {
      // Always take the mode explicitly so a stale closure can never
      // silently flip incremental into a full reindex.
      setRebuildMode(mode)
      setRebuildStarting(true)
      setStatusError(null)
      try {
        const res = await fetch(
          rebuildUrl(selection, mode === "full", Date.now()),
          {
            method: "POST",
            cache: "no-store",
          }
        )
        if (!res.ok) throw new Error(`API returned ${res.status}`)
        const snap = (await res.json()) as RebuildProgress
        setRebuildProgress(snap)
        if (snap.jobId) {
          try { localStorage.setItem(REBUILD_JOB_KEY, snap.jobId) } catch { /* ignore */ }
        }
      } catch (e) {
        setStatusError(e instanceof Error ? e.message : "Rebuild failed")
      } finally {
        setRebuildStarting(false)
      }
    },
    [selection]
  )

  // A final snapshot also refetches the tiles, so the numbers stay correct
  // even if the SSE channel is down and the per-root notifications were lost.
  // When the job finishes cleanly, the finished-report banner auto-dismisses
  // after a few seconds; a report with failed projects stays visible so the
  // failures can actually be read.
  const handleRebuildProgress = useCallback(
    (progress: RebuildProgress) => {
      setRebuildProgress(progress)
      if (progress.jobId) {
        try {
          if (progress.isRunning) {
            localStorage.setItem(REBUILD_JOB_KEY, progress.jobId)
          } else {
            localStorage.removeItem(REBUILD_JOB_KEY)
          }
        } catch {
          // ignore storage errors
        }
      }
      if (!progress.isRunning) void loadStatus()
    },
    [loadStatus]
  )

  // Keep SSE subscription for real-time updates (primary channel).
  useCodebaseRebuildSse(handleRebuildProgress)

  // Poll the job status as a fallback for the SSE stream: the stream can drop
  // or miss the terminal frame (proxy buffering, late subscriber), leaving
  // `rebuilding` stuck true forever. Polling guarantees the terminal state
  // lands even when SSE does not.
  useEffect(() => {
    if (!rebuildProgress?.isRunning || !rebuildProgress.jobId) return
    const jobId = rebuildProgress.jobId
    const interval = setInterval(async () => {
      try {
        const res = await fetch(`/api/codebase/rebuild-index/${jobId}`, { cache: "no-store" })
        if (!res.ok) return
        const snap = (await res.json()) as RebuildProgress
        setRebuildProgress(snap)
        if (!snap.isRunning) {
          clearInterval(interval)
          void loadStatus()
        }
      } catch {
        // ignore transient errors; next tick retries
      }
    }, 2000)
    return () => clearInterval(interval)
  }, [rebuildProgress?.isRunning, rebuildProgress?.jobId, loadStatus])

  // Re-attach to an in-flight rebuild after a page refresh: the POST response
  // (and the SSE stream) are lost on reload, but the backend job keeps running
  // and its snapshot is still fetchable by jobId.
  useEffect(() => {
    let cancelled = false
    const restore = async () => {
      let jobId: string | null = null
      try { jobId = localStorage.getItem(REBUILD_JOB_KEY) } catch { return }
      if (!jobId) return
      try {
        const res = await fetch(`/api/codebase/rebuild-index/${jobId}`, { cache: "no-store" })
        if (!res.ok) throw new Error("not found")
        const snap = (await res.json()) as RebuildProgress
        if (cancelled) return
        setRebuildProgress(snap)
        // The pointer only exists to re-attach to an in-flight rebuild, so drop
        // it once the job is done. Auto-hiding a clean report is the shared
        // effect below; a timer here would duplicate that rule and outlive the
        // component.
        if (!snap.isRunning) {
          try { localStorage.removeItem(REBUILD_JOB_KEY) } catch { /* ignore */ }
        }
      } catch {
        // Job gone (backend restarted, or unknown id): drop the stale pointer.
        try { localStorage.removeItem(REBUILD_JOB_KEY) } catch { /* ignore */ }
      }
    }
    void restore()
    return () => { cancelled = true }
  }, [])

  // A clean finished report is transient: it confirms the rebuild ran, then
  // gets out of the way. Errors are not transient — a failing project's
  // message must stay on screen until the user dismisses it, so only a
  // report with no failed projects is auto-hidden.
  useEffect(() => {
    if (!rebuildProgress || rebuildProgress.isRunning) return
    if (rebuildProgress.results.some((r) => !r.ok)) return
    const timer = setTimeout(() => setRebuildProgress(null), 5000)
    return () => clearTimeout(timer)
  }, [rebuildProgress])

  // The scope travels with the query rather than being read from the
  // callback's closure: every request states which scope it is for, so a
  // response can never be filed under the wrong one.
  const doSearch = useCallback(async (
    q: string,
    scope: SearchScope,
    opts?: { silent?: boolean; selection?: WorkspaceSelection }
  ) => {
    const trimmed = q.trim()
    const seq = ++searchSeq.current
    if (!trimmed) {
      setResults([])
      setFileResults([])
      setSearchError(null)
      setSearching(false)
      setTabsState((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, { ...current, lastStats: undefined }),
        }
      })
      return
    }
    const targetSelection = opts?.selection ?? selection
    const silent = opts?.silent ?? false
    if (!silent) setSearching(true)
    setSearchError(null)
    try {
      const res = await fetch(searchUrl(trimmed, targetSelection, scope), {
        cache: "no-store",
      })
      if (!res.ok) throw new Error(`API returned ${res.status}`)
      const data = await res.json()
      if (searchSeq.current !== seq) return

      const stats: SearchStats | undefined = data.stats
        ? {
            tookMs: Number(data.stats.tookMs) || 0,
            hits: Number(data.stats.hits) || 0,
            files: Number(data.stats.files) || 0,
            chunks: Number(data.stats.chunks) || 0,
            workspaces: Number(data.stats.workspaces) || 1,
            workspacesWithHits: Number(data.stats.workspacesWithHits) || 0,
            mode: data.mode === "files" ? "files" : "search",
          }
        : undefined

      setTabsState((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, { ...current, lastStats: stats }),
        }
      })

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
        setTabsState((prev) => {
          const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
          return {
            ...prev,
            tabs: updateTab(prev.tabs, { ...current, lastStats: undefined }),
          }
        })
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
  // sees the changed scope.
  const applySearchScope = useCallback(
    (next: SearchScope) => {
      if (next === searchScope) return
      setResults([])
      setFileResults([])
      setTabsState((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, { ...current, scope: next, lastStats: undefined }),
        }
      })
      writeUrl({ tab: activeTab.id, selection, scope: next, query, file: selectedFile })
    },
    [searchScope, activeTab.id, selection, query, selectedFile, writeUrl]
  )

  // Clearing the box resets the list immediately (in the event handler,
  // not in an effect, to avoid cascading renders).
  const handleQueryChange = useCallback(
    (value: string) => {
      setQuery(value)
      rememberQuery(value)
      setTabsState((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, {
            ...current,
            query: value,
            ...(!value.trim() ? { lastStats: undefined } : {}),
          }),
        }
      })
      if (!value.trim()) {
        if (debounceTimer.current) clearTimeout(debounceTimer.current)
        searchSeq.current++
        setIssuedQuery("")
        setResults([])
        setFileResults([])
        setSearchError(null)
        setSearching(false)
      }
    },
    []
  )

  const activateTab = useCallback(
    (target: SearchTab, opts?: { file?: SelectedFile | null; newTabs?: SearchTab[] }) => {
      pendingNavTabId.current = target.id
      if (debounceTimer.current) clearTimeout(debounceTimer.current)
      searchSeq.current++
      setTabsState((prev) => ({
        tabs: opts?.newTabs ?? prev.tabs,
        activeId: target.id,
      }))
      setQuery(target.query)
      setIssuedQuery(target.query)
      rememberQuery(target.query)
      writeUrl({
        tab: target.id,
        selection: target.selection,
        scope: target.scope,
        query: target.query,
        file: opts?.file ?? null,
      })
      if (target.query.trim()) {
        void doSearch(target.query, target.scope, { selection: target.selection })
      } else {
        setResults([])
        setFileResults([])
      }
    },
    [doSearch, writeUrl]
  )

  const selectTab = useCallback(
    (targetId: string) => {
      if (targetId === activeTabId) return
      const target = tabs.find((t) => t.id === targetId)
      if (target) activateTab(target, { file: selectedFile })
    },
    [activeTabId, tabs, selectedFile, activateTab]
  )

  const addSearchTab = useCallback(() => {
    const newTab = createTab({ selection: activeTab.selection })
    activateTab(newTab, { file: null, newTabs: [...tabs, newTab] })
  }, [tabs, activeTab.selection, activateTab])

  const closeSearchTab = useCallback(
    (closeId: string) => {
      const res = closeTab(tabs, closeId, activeTabId)
      const nextActive = res.tabs.find((t) => t.id === res.activeId) ?? res.tabs[0]
      activateTab(nextActive, { file: null, newTabs: res.tabs })
    },
    [tabs, activeTabId, activateTab]
  )

  // Sync active tab on browser back/forward navigation (popstate).
  useEffect(() => {
    const handlePopState = () => {
      const params = new URLSearchParams(window.location.search)
      const tabId = tabFromParams(params)
      if (tabId && tabId !== activeTabId) {
        const target = tabs.find((t) => t.id === tabId)
        if (target) {
          activateTab(target, { file: selectedFile })
        }
      }
    }
    window.addEventListener("popstate", handlePopState)
    return () => window.removeEventListener("popstate", handlePopState)
  }, [tabs, activeTabId, activateTab, selectedFile])

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
    if (pendingNavTabId.current) return
    if (queryFromParams(searchParams) === issuedQuery) return
    writeUrl({
      tab: activeTab.id,
      selection,
      scope: searchScope,
      query: issuedQuery,
      file: selectedFile,
    })
  }, [issuedQuery, searchParams, selection, searchScope, selectedFile, writeUrl, activeTab.id])

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
            disabled={statusLoading || rebuilding}
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
              onClick={() => void rebuild(rebuildMode)}
              title={`Rebuild index (${rebuildModeLabel(rebuildMode)})`}
              aria-label={`Rebuild index in ${rebuildModeLabel(rebuildMode)} mode`}
            >
              <RefreshCw
                className={rebuilding ? "size-4 animate-spin" : "size-4"}
              />
              <span className="hidden sm:inline-block">
                {rebuilding ? "Indexing…" : `Rebuild ${rebuildModeLabel(rebuildMode)}`}
              </span>
            </Button>
            <DropdownMenu
              open={rebuildModeMenuOpen}
              onOpenChange={(open) => {
                // Block opening (the mode cannot change mid-rebuild) but never
                // block closing: a controlled menu that ignores a close request
                // cannot be dismissed by clicking outside.
                if (rebuilding && open) return
                setRebuildModeMenuOpen(open)
              }}
            >
              <DropdownMenuTrigger
                disabled={rebuilding}
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
                  onValueChange={(value) => {
                    // Picking a mode is an action, not a toggle: apply it and
                    // start the rebuild straight away, so the drop-down behaves
                    // like a direct command instead of a two-step picker.
                    const mode = value as RebuildMode
                    setRebuildMode(mode)
                    setRebuildModeMenuOpen(false)
                    void rebuild(mode)
                  }}
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

        <div className="sticky top-16 z-10 flex flex-col gap-2.5 bg-background py-1.5">
          <SearchTabsBar
            tabs={tabs}
            activeId={activeTabId}
            onSelectTab={selectTab}
            onCloseTab={closeSearchTab}
            onAddTab={addSearchTab}
          />

          <form
            className="flex flex-wrap gap-2"
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
        </div>
        <p className="-mt-2 text-xs text-muted-foreground">
          {SEARCH_SCOPE_COPY[searchScope].hint}
        </p>

        {activeTab.lastStats && query.trim() && (
          <SearchStatsRibbon
            stats={activeTab.lastStats}
            scope={searchScope}
            limit={20}
          />
        )}

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
