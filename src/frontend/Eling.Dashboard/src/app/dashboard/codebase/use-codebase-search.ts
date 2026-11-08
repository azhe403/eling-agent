"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { useSearchParams } from "next/navigation"
import {
  queryFromParams,
  searchScopeFromParams,
  searchUrl,
  selectionFromParams,
  tabFromParams,
  type SearchScope,
} from "./scope"
import {
  createTab,
  closeTab,
  loadTabs,
  saveTabs,
  updateTab,
  type SearchTab,
  type SearchStats,
} from "./searchTabs"
import { rememberQuery } from "./searchPrefs"
import type { WorkspaceSelection } from "@/components/workspaces/selection"
import type { SelectedFile } from "./fileDetailApi"

/** Idle time after the last keystroke before a search is issued. */
export const SEARCH_DEBOUNCE_MS = 500

/** A content hit: the snippet that matched, and the line range it sits in. */
export type CodebaseHit = {
  projectRoot: string
  path: string
  startLine: number
  endLine: number
  content: string
  score: number
}

/** The URL state a search tab carries. */
export interface UseCodebaseSearchOptions {
  /** Flips the skeleton while a new selection loads. */
  setStatusLoading: React.Dispatch<React.SetStateAction<boolean>>
  /** The scope the search runs over, when the caller does not name one. */
  defaultScope: SearchScope
  /** Writes the page's URL state; the search tab is part of it. */
  writeUrl: (next: {
    tab: string
    selection: WorkspaceSelection
    scope: SearchScope
    query: string
    file: SelectedFile | null
  }) => void
  /** The file open in the viewer, if any. */
  selectedFile: SelectedFile | null
}

/**
 * The search: the tabs, the query, and the rows they show.
 *
 * A search tab keeps its own query, scope and selection, so switching tabs
 * brings back the search that tab was left on rather than the last one typed.
 * The results of a slow request are dropped when a newer request has already
 * been issued, so an earlier answer can never overwrite a later one.
 */
export function useCodebaseSearch({
  setStatusLoading,
  defaultScope,
  writeUrl,
  selectedFile,
}: UseCodebaseSearchOptions) {
  const searchParams = useSearchParams()

  // The tab the URL names, if any.
  const urlTabId = useMemo(() => tabFromParams(searchParams), [searchParams])

  const [tabsState, setTabsState] = useState(() => {
    // The tabs come from the URL on the first render, so a shared link opens
    // the same tabs the sender had.
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

  // The query comes from the active tab.
  const [query, setQuery] = useState(() => activeTab?.query ?? "")

  const [results, setResults] = useState<CodebaseHit[]>([])
  const [fileResults, setFileResults] = useState<import("./FileResultList").FileResult[]>([])
  const [searchError, setSearchError] = useState<string | null>(null)
  const [searching, setSearching] = useState(false)
  const [searchLimit, setSearchLimit] = useState(20)

  // Monotonic id: only the latest issued search may write results, so a slow
  // earlier response can never overwrite a newer one.
  const searchSeq = useRef(0)
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  // Tracks in-flight tab navigation initiated by user interaction to prevent
  // the URL back/forward sync effect from reverting back to the old URL tab id
  // before router.replace finishes updating searchParams.
  const pendingNavTabId = useRef<string | null>(null)
  // The scope the results on screen belong to. A scope change is an explicit
  // action rather than a keystroke, so it searches at once instead of
  // waiting out the debounce a typed character gets.
  const shownScope = useRef<SearchScope>(defaultScope)

  // The query as it was when the last search went out.
  const [issuedQuery, setIssuedQuery] = useState("")

  const selection = activeTab?.selection ?? { kind: "all" as const }
  const searchScope = activeTab?.scope ?? defaultScope

  // Persist tab state whenever tabs or active tab changes, kept out of state
  // updaters.
  useEffect(() => {
    saveTabs(tabsState.tabs, tabsState.activeId)
  }, [tabsState])

  /**
   * The scope travels with the query rather than being read from the
   * callback's closure: every request states which scope it is for, so a
   * response can never be filed under the wrong one.
   */
  const doSearch = useCallback(
    async (
      q: string,
      scope: SearchScope,
      opts?: { silent?: boolean; selection?: WorkspaceSelection; limit?: number }
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
      const effectiveLimit = opts?.limit ?? searchLimit
      if (!silent) setSearching(true)
      setSearchError(null)
      try {
        const res = await fetch(searchUrl(trimmed, targetSelection, scope, effectiveLimit), {
          cache: "no-store",
        })
        if (!res.ok) throw new Error(`API returned ${res.status}`)
        const data = await res.json()
        if (searchSeq.current !== seq) return

        const stats: SearchStats | undefined = data.stats
          ? {
              tookMs: Number(data.stats.tookMs) || 0,
              hits: Number(data.stats.hits) || 0,
              totalHits: Number(data.stats.totalHits) || Number(data.stats.hits) || 0,
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
    },
    [selection, searchLimit]
  )

  /** Brings a tab back on screen, with its own query and scope. */
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
    const newTab = createTab({ selection })
    activateTab(newTab, { file: null, newTabs: [...tabs, newTab] })
  }, [tabs, selection, activateTab])

  const closeSearchTab = useCallback(
    (closeId: string) => {
      const res = closeTab(tabs, closeId, activeTabId)
      const nextActive = res.tabs.find((t) => t.id === res.activeId) ?? res.tabs[0]
      activateTab(nextActive, { file: null, newTabs: res.tabs })
    },
    [tabs, activeTabId, activateTab]
  )

  /** Replaces the tabs, keeping the active one unless it is gone. */
  const updateTabs = useCallback(
    (next: React.SetStateAction<{ tabs: SearchTab[]; activeId: string }>) =>
      setTabsState(next as React.SetStateAction<{ tabs: SearchTab[]; activeId: string }>),
    []
  )

  /**
   * The query box changed. Clearing it resets the list immediately, in the
   * event handler, so the rows go without waiting for a render pass.
   */
  const handleQueryChange = useCallback(
    (value: string) => {
      setQuery(value)
      rememberQuery(value)
      updateTabs((prev) => {
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
    [updateTabs]
  )
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
  /** The result limit changed; the search runs again under the new limit. */
  const handleLimitChange = useCallback(
    (newLimit: number) => {
      setSearchLimit(newLimit)
      if (query.trim()) {
        void doSearch(query, searchScope, { limit: newLimit })
      }
    },
    [query, searchScope, doSearch]
  )

  /**
   * Switching the scope drops the rows on screen and puts the new scope in
   * the URL; the search that follows belongs to the debounce effect, which
   * sees the changed scope.
   */
  const applySearchScope = useCallback(
    (next: SearchScope) => {
      if (next === searchScope) return
      setResults([])
      setFileResults([])
      updateTabs((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, { ...current, scope: next, lastStats: undefined }),
        }
      })
      writeUrl({
        tab: activeTabId,
        selection,
        scope: next,
        query,
        file: selectedFile,
      })
    },
    [searchScope, activeTabId, selection, query, selectedFile, writeUrl, updateTabs]
  )

  /**
   * A selection change flips the skeleton on right in the event handler —
   * before the router round-trip — so tiles react to the click, not to the
   * next commit.
   */
  const applySelection = useCallback(
    (next: WorkspaceSelection) => {
      setStatusLoading(true)
      updateTabs((prev) => {
        const current = prev.tabs.find((t) => t.id === prev.activeId) ?? prev.tabs[0]
        return {
          ...prev,
          tabs: updateTab(prev.tabs, { ...current, selection: next }),
        }
      })
      writeUrl({
        tab: activeTabId,
        selection: next,
        scope: searchScope,
        query,
        file: selectedFile,
      })
    },
    [writeUrl, searchScope, query, selectedFile, activeTabId, updateTabs]
  )
  /** Opens a file in the viewer, keeping the tab it belongs to. */
  const openFile = useCallback(
    (file: SelectedFile) =>
      writeUrl({
        tab: activeTabId,
        selection,
        scope: searchScope,
        query,
        file,
      }),
    [writeUrl, activeTabId, selection, searchScope, query]
  )

  /** Closes the viewer, keeping everything else. */
  const closeFile = useCallback(
    () => writeUrl({ tab: activeTabId, selection, scope: searchScope, query, file: null }),
    [writeUrl, activeTabId, selection, searchScope, query]
  )
  return {
    tabs,
    updateTabs,
    activeTabId,
    activeTab,
    selection,
    searchScope,
    query,
    setQuery,
    issuedQuery,
    setIssuedQuery,
    results,
    fileResults,
    setResults,
    setFileResults,
    searchError,
    setSearchError,
    searching,
    setSearching,
    searchLimit,
    setSearchLimit,
    searchSeq,
    debounceTimer,
    pendingNavTabId,
    shownScope,
    doSearch,
    activateTab,
    selectTab,
    addSearchTab,
    closeSearchTab,
    handleLimitChange,
    applySearchScope,
    handleQueryChange,
    applySelection,
    openFile,
    closeFile,
    urlTabId,
  }
}
