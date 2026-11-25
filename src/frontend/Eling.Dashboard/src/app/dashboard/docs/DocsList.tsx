"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { useRouter, useSearchParams } from "next/navigation"
import { Plus, RefreshCw, Search, ChevronLeft, ChevronRight, ScanSearch, Loader2 } from "lucide-react"

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
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogTitle,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { LoadingBar } from "@/components/ui/loading-bar"
import { useDocsSse } from "@/hooks/use-docs-sse"
import { useWorkspaceRows } from "@/components/workspaces/use-workspace-rows"
import { WorkspaceScopePanel } from "@/components/workspaces/workspace-scope-panel"
import { WorkspaceSelect } from "@/components/workspaces/workspace-select"
import { selectAll, selectOnly, toggleRoots, type WorkspaceSelection } from "@/components/workspaces/selection"
import { useMediaQuery } from "@/hooks/use-media-query"
import { usePersistedState, useResumePageParams } from "@/lib/persisted-state"
import { XL_BREAKPOINT } from "@/lib/constants"
import { apiFetch, ApiError } from "@/lib/api"
import type {
  DocListItem,
  DocListResponse,
  DocTopicGroup,
  DocKindDto,
  DocReadResult,
  RelationKindDto,
} from "@/lib/types"

import { DocCard } from "./DocCard"
import { DocDetailPanel, type DocFormData } from "./DocDetailPanel"
import {
  docKinds,
  docKindLabel,
  extractTopics,
  applySelection,
  selectionFromParams,
  sortFromParams,
  SORT_KEYS,
  DEFAULT_SORT,
  DEFAULT_ORDER,
  type DocSortKey,
  type DocSortOrder,
} from "./docs-scope"

const KINDS = ["all", "spec", "plan", "doc", "scratch"] as const
const ORDERS = ["asc", "desc"] as const

type KindFilter = (typeof KINDS)[number]
type OrderFilter = (typeof ORDERS)[number]

/** Segmented scope selector — same convention as Skills/Memories filter groups. */
function FilterGroup<T extends string>({
  label,
  options,
  value,
  onChange,
  trailing,
}: {
  label: string
  options: readonly T[]
  value: T
  onChange: (next: T) => void
  trailing?: React.ReactNode
}) {
  return (
    <div className="flex shrink-0 items-center gap-1 rounded-lg border bg-muted/40 p-1" role="group" aria-label={label}>
      {options.map((option) => (
        <button
          key={option}
          type="button"
          aria-pressed={value === option}
          onClick={() => onChange(option)}
          className={
            (value === option
              ? "rounded-md bg-background px-2.5 py-1 text-xs font-medium text-foreground shadow-sm"
              : "rounded-md px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground") +
            " whitespace-nowrap capitalize"
          }
        >
          {option === "all" ? "All" : option.charAt(0).toUpperCase() + option.slice(1)}
        </button>
      ))}
      {trailing && (
        <>
          <span aria-hidden className="mx-0.5 h-4 w-px bg-border" />
          {trailing}
        </>
      )}
    </div>
  )
}

export function DocsList() {
  const searchParams = useSearchParams()
  const router = useRouter()
  const { rows: workspaceRows } = useWorkspaceRows()

  const [topics, setTopics] = useState<DocTopicGroup[]>([])
  const [total, setTotal] = useState(0)
  const [matching, setMatching] = useState(0)
  const [loading, setLoading] = useState(true)
  const [rescanning, setRescanning] = useState(false)
  const [hasLoadedOnce, setHasLoadedOnce] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Canonical per-page persistence: URL is the source of truth, localStorage
  // remembers the last position on a bare revisit. Matches Skills/Memories.
  const [kind, setKind] = usePersistedState<KindFilter>({ page: "docs", param: "kind", defaultValue: "all", allowed: KINDS })
  const [topic, setTopic] = usePersistedState<string>({ page: "docs", param: "topic", defaultValue: "" })
  const initialSort = sortFromParams(searchParams)
  const [sort, setSort] = usePersistedState<DocSortKey>({ page: "docs", param: "sort", defaultValue: initialSort.sort, allowed: SORT_KEYS })
  const [order, setOrder] = usePersistedState<OrderFilter>({ page: "docs", param: "order", defaultValue: initialSort.order, allowed: ORDERS })
  useResumePageParams("docs", [
    { param: "kind", defaultValue: "all" },
    { param: "topic", defaultValue: "" },
    { param: "sort", defaultValue: DEFAULT_SORT },
    { param: "order", defaultValue: DEFAULT_ORDER },
    { param: "doc", defaultValue: "" },
  ])
  const [workspaceSelection, setWorkspaceSelection] = useState<WorkspaceSelection>(() => selectionFromParams(searchParams))
  const [limit] = useState(50)
  const [offset, setOffset] = useState(() => parseInt(searchParams.get("offset") ?? "0", 10))

  // Detail selection follows the dashboard master-detail standard: the id is
  // persisted (URL + localStorage) and the item is derived from the loaded list.
  const [selectedDocId, setSelectedDocId] = usePersistedState<string>({
    page: "docs",
    param: "doc",
    defaultValue: "",
  })
  const [detailState, setDetailState] = useState<{ id: string; doc: DocReadResult | null; error: string | null; loading: boolean } | null>(null)
  const [detailNonce, setDetailNonce] = useState(0)
  const [createMode, setCreateMode] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<DocListItem | null>(null)

  // Available topics from the loaded list
  const availableTopics = useMemo(() => extractTopics(topics), [topics])
  const allRoots = useMemo(() => workspaceRows.map((row) => row.root), [workspaceRows])
  const allDocs = useMemo(() => topics.flatMap((t) => t.documents), [topics])
  const selectedListItem = useMemo(
    () => (selectedDocId === "" ? null : (allDocs.find((d) => d.id === selectedDocId) ?? null)),
    [allDocs, selectedDocId],
  )

  const applyWorkspaceSelection = useCallback((next: WorkspaceSelection) => {
    setWorkspaceSelection(next)
    const params = applySelection(new URLSearchParams(searchParams.toString()), next)
    router.replace(`/dashboard/docs?${params.toString()}`)
  }, [router, searchParams])

  // Error banner auto-dismiss
  useEffect(() => {
    if (error === null) return
    const t = setTimeout(() => setError(null), 5000)
    return () => clearTimeout(t)
  }, [error])

  // The narrow-screen detail dialog is portaled into this content area instead
  // of <body>. A body-level portal makes Base UI's modal mark everything else
  // inert — including the app sidebar — so the sidebar looks closed when a card
  // is clicked. Scoping the portal (and overlay) to the content column keeps the
  // sidebar mounted, visible and interactive.
  const dialogContainerRef = useRef<HTMLDivElement>(null)

  // Detail fetch
  const selectedId = selectedListItem?.id ?? null

  const load = useCallback(async () => {
    try {
      const params = new URLSearchParams()
      params.set("limit", String(limit))
      params.set("offset", String(offset))
      if (kind !== "all") params.set("kind", kind)
      if (workspaceSelection.kind === "workspaces") {
        for (const root of workspaceSelection.roots) params.append("workspace", root)
      }
      if (topic) params.set("topic", topic)
      params.set("sort", sort)
      params.set("order", order)

      const data = await apiFetch<DocListResponse>(`/api/docs?${params}`, {
        cache: "no-store",
        headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
      })

      // The list endpoint names the identifier `docId`; the detail endpoint and
      // every selection handler use `id`. Normalise once here so a card can be
      // selected (and its document fetched) at all.
      const normalize = (d: DocListItem): DocListItem => ({ ...d, id: d.id || d.docId || "" })

      const topicGroups = data?.topics ?? []
      const allItems = topicGroups.flatMap((t) => t.documents).map(normalize)
      const normalizedGroups = topicGroups.map((t) => ({ ...t, documents: t.documents.map(normalize) }))

      setTopics(normalizedGroups)
      setTotal(data?.total ?? allItems.length)
      setMatching(allItems.length)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load documents")
    } finally {
      setLoading(false)
      setHasLoadedOnce(true)
    }
  }, [kind, workspaceSelection, topic, sort, order, limit, offset])

  const handleDocsChanged = useCallback(() => {
    void load()
    setDetailNonce((n) => n + 1)
  }, [load])

  const { status: sseStatus } = useDocsSse(handleDocsChanged, ["docs"])

  const handleRescan = useCallback(async () => {
    setRescanning(true)
    setError(null)
    try {
      await apiFetch("/api/docs/reindex", { method: "POST" })
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Rescan failed")
    } finally {
      setRescanning(false)
    }
  }, [load])

  const handleAddRelation = useCallback(async (sourceId: string, targetId: string, kind: RelationKindDto) => {
    // Backend expects the enum name, not the numeric DTO value.
    const kindName = ["Implements", "Supersedes", "References"][kind]
    await apiFetch(`/api/docs/${encodeURIComponent(sourceId)}/relation`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targetId, kind: kindName }),
    })
    setDetailNonce((n) => n + 1)
  }, [])

  useEffect(() => {
    if (!selectedId) {
      // Nothing selected: the derived values below already read as "no detail",
      // so there is no state to reset here. Setting it synchronously inside the
      // effect would trigger a cascading render for no benefit.
      return
    }
    let cancelled = false
    const run = async () => {
      setDetailState({ id: selectedId, doc: null, error: null, loading: true })
      try {
        const result = await apiFetch<DocReadResult>(`/api/docs/${encodeURIComponent(selectedId)}`, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
        })
        if (cancelled) return
        setDetailState({ id: selectedId, doc: result, error: null, loading: false })
      } catch (e) {
        if (cancelled) return
        setDetailState({
          id: selectedId,
          doc: null,
          error: e instanceof ApiError && e.status === 404 ? "Document not found" : e instanceof Error ? e.message : "Failed to load document",
          loading: false,
        })
      }
    }
    void run()
    return () => { cancelled = true }
  }, [selectedId, detailNonce])

  // Guard against a stale doc flashing while the selection changes: only trust
  // the detail once it belongs to the currently selected id (Skill pattern).
  const detailReady = selectedId !== null && detailState?.id === selectedId && detailState?.loading === false
  const detail = detailReady ? detailState!.doc : null
  const detailError = detailReady ? detailState!.error : null
  const detailLoading = selectedId !== null && (detailState?.id !== selectedId || detailState?.loading !== false)

  useEffect(() => {
    let isMounted = true
    const fetchAll = async () => {
      if (isMounted) await load()
    }
    void fetchAll()
    return () => { isMounted = false }
  }, [load])

  const handleSelect = (doc: DocListItem) => {
    setCreateMode(false)
    // Selecting is a one-way action: clicking the already-open card keeps its
    // detail in place. Closing is the panel's explicit X button (or the narrow
    // dialog's dismiss), never a second click on the card — otherwise a stray
    // click on the row you're reading makes the detail vanish.
    setSelectedDocId(doc.id)
  }

  const handleNewDoc = () => {
    setSelectedDocId("")
    setCreateMode(true)
  }

  const handleSaveEdit = useCallback(async (id: string, data: DocFormData) => {
    await apiFetch(`/api/docs/${encodeURIComponent(id)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    })
    setDetailNonce((n) => n + 1)
    await load()
  }, [load])

  const handleCreate = useCallback(async (data: DocFormData) => {
    await apiFetch("/api/docs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    })
    setCreateMode(false)
    await load()
  }, [load])

  const handleDelete = useCallback(async () => {
    if (!deleteTarget) return
    const target = deleteTarget
    setDeleteTarget(null)
    try {
      await apiFetch(`/api/docs/${encodeURIComponent(target.id)}`, { method: "DELETE" })
      if (selectedDocId === target.id) setSelectedDocId("")
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed")
    }
  }, [deleteTarget, load])

  const handlePromote = useCallback(async (id: string) => {
    try {
      await apiFetch(`/api/docs/${encodeURIComponent(id)}/promote`, { method: "POST" })
      setDetailNonce((n) => n + 1)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : "Promote failed")
    }
  }, [load])

  const panel = (variant: "aside" | "stacked") => (
    <DocDetailPanel
      key={createMode ? "create" : (selectedListItem?.id ?? "none")}
      variant={variant}
      summary={selectedListItem}
      detail={detail}
      detailLoading={detailLoading}
      detailError={detailError}
      createMode={createMode}
      trashMode={false}
      onClose={() => { setSelectedDocId(""); setCreateMode(false) }}
      onSaveEdit={handleSaveEdit}
      onCreate={handleCreate}
      onDelete={handleDelete}
      onRetry={() => setDetailNonce((n) => n + 1)}
      onPromote={handlePromote}
      onAddRelation={handleAddRelation}
    />
  )

  const wide = useMediaQuery(`(min-width: ${XL_BREAKPOINT}px)`)
  const panelOpen = wide && (selectedListItem !== null || createMode)
  const mainColClass = panelOpen
    ? "flex h-full min-h-0 w-[420px] shrink-0 flex-col gap-4 overflow-y-auto p-4 pt-0"
    : "flex h-full min-h-0 min-w-0 flex-1 flex-col gap-4 overflow-y-auto p-4 pt-0"

  return (
    <div ref={dialogContainerRef} className="relative flex h-[100dvh] w-full flex-col overflow-hidden">
      <DashboardHeader label="Docs">
        <div
          className="flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[11px] font-medium border border-border/50 bg-muted/30"
          title={sseStatus === "connected" ? "SSE Connected: Live stream active" : "SSE: Connecting..."}
        >
          <span className={`inline-block size-2 rounded-full ${
            sseStatus === "connected" ? "bg-green-500 animate-pulse"
              : sseStatus === "connecting" ? "bg-amber-500" : "bg-destructive"
          }`} />
          <span className="text-muted-foreground">
            {sseStatus === "connected" ? "Live" : sseStatus === "connecting" ? "Connecting" : "Offline"}
          </span>
        </div>

        <span className="hidden sm:inline-block text-xs text-muted-foreground font-medium">
          {matching} of {total}
        </span>

        <Button
          variant="outline"
          size="sm"
          onClick={() => void handleRescan()}
          disabled={rescanning}
          title="Rescan the docs directories and refresh the index"
        >
          {rescanning ? <Loader2 className="size-4 animate-spin" /> : <ScanSearch className="size-4" />}
          <span className="hidden sm:inline-block">Rescan</span>
        </Button>

        <Button
          variant="outline"
          size="sm"
          onClick={() => { setLoading(true); void load() }}
          disabled={loading}
        >
          <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
          <span className="hidden sm:inline-block">Refresh</span>
        </Button>

        <Button size="sm" onClick={handleNewDoc} title="New Document" aria-label="New Document">
          <Plus className="size-4" />
          <span className="hidden sm:inline-block">New Doc</span>
        </Button>
      </DashboardHeader>

      <div className="flex min-h-0 w-full flex-1 overflow-hidden">
        <WorkspaceScopePanel
          rows={workspaceRows}
          selection={workspaceSelection}
          onSelectAll={() => applyWorkspaceSelection(selectAll())}
          onSelectOnly={(root) => applyWorkspaceSelection(selectOnly(root))}
          onToggleRoots={(roots) => applyWorkspaceSelection(toggleRoots(workspaceSelection, roots, allRoots))}
        />
        <div className={mainColClass}>
          <div className="px-4 pt-2 lg:hidden">
            <WorkspaceSelect
              rows={workspaceRows}
              selection={workspaceSelection}
              onSelectAll={() => applyWorkspaceSelection(selectAll())}
              onSelectOnly={(root) => applyWorkspaceSelection(selectOnly(root))}
              onToggleRoots={(roots) => applyWorkspaceSelection(toggleRoots(workspaceSelection, roots, allRoots))}
            />
          </div>
          {error && (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
              {error}
            </div>
          )}

          {/* Toolbar: filters */}
          <div className="sticky top-0 z-10 -mb-4 flex flex-col gap-2 bg-background pb-4 pt-4">
            <div className="flex flex-wrap items-center gap-2">
              <div className={`relative min-w-[200px] flex-1 ${panelOpen ? "basis-full" : ""}`}>
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-4 text-muted-foreground" />
                <Input
                  aria-label="Filter by topic"
                  placeholder="Filter by topic..."
                  value={topic}
                  onChange={(e) => { setOffset(0); setTopic(e.target.value) }}
                  className="pl-9"
                />
              </div>

              <FilterGroup
                label="Kind"
                options={KINDS}
                value={kind}
                onChange={(v) => { setOffset(0); setKind(v) }}
              />
              <FilterGroup
                label="Sort"
                options={SORT_KEYS}
                value={sort}
                onChange={(v) => { setOffset(0); setSort(v) }}
                trailing={
                  <button
                    type="button"
                    aria-label={`Sort direction ${order}`}
                    title={order === "asc" ? "Ascending" : "Descending"}
                    onClick={() => { setOffset(0); setOrder(order === "asc" ? "desc" : "asc") }}
                    className="rounded-md px-2 py-1 text-xs font-medium text-muted-foreground hover:text-foreground"
                  >
                    {order === "asc" ? "↑" : "↓"}
                  </button>
                }
              />

              {availableTopics.length > 0 && (
                <Select value={topic} onValueChange={(v) => { setOffset(0); setTopic(v ?? "") }}>
                  <SelectTrigger className="w-[180px]">
                    <SelectValue placeholder="Topic" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="">All topics</SelectItem>
                    {availableTopics.map((t) => (
                      <SelectItem key={t} value={t}>{t}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
            </div>
          </div>

          <div className="relative min-w-0">
            <LoadingBar loading={loading} />
            {allDocs.length === 0 && !loading ? (
              <div className="flex min-h-[200px] flex-col items-center justify-center gap-2 rounded-xl border border-dashed text-sm text-muted-foreground">
                {total > 0 ? "No documents match this filter." : "No documents found."}
              </div>
            ) : allDocs.length > 0 ? (
              <div className={panelOpen ? "grid grid-cols-1 gap-2.5" : "grid grid-cols-1 gap-2.5 lg:grid-cols-2"}>
                {allDocs.map((doc) => (
                  <DocCard
                    key={doc.id}
                    doc={doc}
                    selected={selectedDocId === doc.id}
                    onSelect={handleSelect}
                  />
                ))}
              </div>
            ) : null}

            {/* Pagination */}
            {(total > limit || offset > 0) && (
              <div className="mt-4 flex items-center justify-center gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setOffset(Math.max(0, offset - limit))}
                  disabled={offset === 0 || loading}
                >
                  <ChevronLeft className="size-4" />
                  Previous
                </Button>
                <span className="text-sm text-muted-foreground">
                  Page {Math.floor(offset / limit) + 1}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setOffset(offset + limit)}
                  disabled={allDocs.length < limit || loading}
                >
                  Next
                  <ChevronRight className="size-4" />
                </Button>
              </div>
            )}
          </div>

          {/* Narrow screens: the panel opens as a dialog over the grid. */}
          {!wide && (selectedListItem || createMode) && (
            <Dialog
              open
              onOpenChange={(open) => {
                if (!open) {
                  setSelectedDocId("")
                  setCreateMode(false)
                }
              }}
            >
              <DialogContent
                showCloseButton={false}
                container={dialogContainerRef}
                overlayClassName="absolute"
                className="absolute max-h-[85svh] w-[94vw] overflow-hidden p-0 sm:max-w-3xl"
              >
                <DialogTitle className="sr-only">
                  {createMode ? "New document" : (selectedListItem?.title ?? "Document detail")}
                </DialogTitle>
                <DialogDescription className="sr-only">
                  {createMode ? "Create a new document" : "Document detail panel"}
                </DialogDescription>
                {panel("stacked")}
              </DialogContent>
            </Dialog>
          )}
        </div>

        {/* Wide screens: master-detail panel docked on the right. */}
        {panelOpen && panel("aside")}
      </div>

      <AlertDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => !open && setDeleteTarget(null)}
      >
        <AlertDialogContent className="w-[94vw] sm:max-w-lg">
          <AlertDialogHeader>
            <AlertDialogTitle className="text-destructive">Delete Document?</AlertDialogTitle>
            <AlertDialogDescription>
              This will archive the document. It can be restored from the trash if needed.
            </AlertDialogDescription>
          </AlertDialogHeader>
          {deleteTarget && (
            <div className="rounded-lg border border-destructive/20 bg-destructive/5 p-3 text-xs text-muted-foreground">
              <span className="font-medium text-foreground">{deleteTarget.title}</span>
              <span className="mx-2">·</span>
              <span className="font-mono">{deleteTarget.path}</span>
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => void handleDelete()}
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}