"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import { Plus, RefreshCw, Search, Filter, ChevronDown, ChevronUp, ChevronLeft, ChevronRight } from "lucide-react"

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
import { Skeleton } from "@/components/ui/skeleton"
import { useDocsSse } from "@/hooks/use-docs-sse"
import { useMediaQuery } from "@/hooks/use-media-query"
import { XL_BREAKPOINT } from "@/lib/constants"
import { apiFetch, ApiError } from "@/lib/api"
import type {
  DocListItem,
  DocListResponse,
  DocTopicGroup,
  DocKindDto,
  DocOwnershipDto,
  DocOriginDto,
  DocReadResult,
} from "@/lib/types"

import { DocCard } from "./DocCard"
import { DocDetailPanel, type DocFormData } from "./DocDetailPanel"
import {
  docKinds,
  docOrigins,
  docOwnerships,
  docKindLabel,
  docOwnershipLabel,
  docOriginLabel,
  extractTopics,
} from "./docs-scope"

const ORIGINS = ["all", "current", "ancestor", "shard", "global"] as const
const OWNERSHIPS = ["all", "managed", "external"] as const
const KINDS = ["all", "spec", "plan", "doc", "scratch"] as const

type OriginFilter = (typeof ORIGINS)[number]
type OwnershipFilter = (typeof OWNERSHIPS)[number]
type KindFilter = (typeof KINDS)[number]

export function DocsList() {
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()

  const [topics, setTopics] = useState<DocTopicGroup[]>([])
  const [total, setTotal] = useState(0)
  const [matching, setMatching] = useState(0)
  const [loading, setLoading] = useState(true)
  const [hasLoadedOnce, setHasLoadedOnce] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [kind, setKind] = useState<KindFilter>(() => (searchParams.get("kind") as KindFilter) ?? "all")
  const [ownership, setOwnership] = useState<OwnershipFilter>(() => (searchParams.get("ownership") as OwnershipFilter) ?? "all")
  const [origin, setOrigin] = useState<OriginFilter>(() => (searchParams.get("origin") as OriginFilter) ?? "all")
  const [topic, setTopic] = useState<string | null>(() => searchParams.get("topic"))
  const [limit] = useState(50)
  const [offset, setOffset] = useState(() => parseInt(searchParams.get("offset") ?? "0", 10))

  const [selectedDoc, setSelectedDoc] = useState<DocListItem | null>(null)
  const [detailState, setDetailState] = useState<{ doc: DocReadResult | null; error: string | null; loading: boolean } | null>(null)
  const [detailNonce, setDetailNonce] = useState(0)
  const [createMode, setCreateMode] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<DocListItem | null>(null)

  // Available topics from the loaded list
  const availableTopics = useMemo(() => extractTopics(topics), [topics])

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

  // URL sync for filters
  const urlSyncReady = useRef(false)
  useEffect(() => {
    if (!urlSyncReady.current) {
      urlSyncReady.current = true
      return
    }
    const params = new URLSearchParams()
    if (kind !== "all") params.set("kind", kind)
    if (ownership !== "all") params.set("ownership", ownership)
    if (origin !== "all") params.set("origin", origin)
    if (topic) params.set("topic", topic)
    if (offset > 0) params.set("offset", String(offset))
    const qs = params.toString()
    if (qs !== searchParams.toString()) {
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    }
  }, [kind, ownership, origin, topic, offset, pathname, router, searchParams])

  // Detail fetch
  const selectedId = selectedDoc?.id ?? null

  const load = useCallback(async () => {
    try {
      const params = new URLSearchParams()
      params.set("limit", String(limit))
      params.set("offset", String(offset))
      if (kind !== "all") params.set("kind", kind)
      if (ownership !== "all") params.set("ownership", ownership)
      if (origin !== "all") params.set("origin", origin)
      if (topic) params.set("topic", topic)

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
      setTotal(allItems.length)
      setMatching(allItems.length)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load documents")
    } finally {
      setLoading(false)
      setHasLoadedOnce(true)
    }
  }, [kind, ownership, origin, topic, limit, offset])

  const handleDocsChanged = useCallback(() => {
    void load()
    setDetailNonce((n) => n + 1)
  }, [load])

  useDocsSse(handleDocsChanged, ["docs"])

  useEffect(() => {
    if (!selectedId) {
      // Nothing selected: the derived values below already read as "no detail",
      // so there is no state to reset here. Setting it synchronously inside the
      // effect would trigger a cascading render for no benefit.
      return
    }
    let cancelled = false
    const run = async () => {
      setDetailState({ doc: null, error: null, loading: true })
      try {
        const result = await apiFetch<DocReadResult>(`/api/docs/${encodeURIComponent(selectedId)}`, {
          cache: "no-store",
          headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
        })
        if (cancelled) return
        setDetailState({ doc: result, error: null, loading: false })
      } catch (e) {
        if (cancelled) return
        setDetailState({
          doc: null,
          error: e instanceof ApiError && e.status === 404 ? "Document not found" : e instanceof Error ? e.message : "Failed to load document",
          loading: false,
        })
      }
    }
    void run()
    return () => { cancelled = true }
  }, [selectedId, detailNonce])

  const detail = selectedId && detailState?.loading === false ? detailState.doc : null
  const detailError = selectedId && detailState?.loading === false ? detailState.error : null
  const detailLoading = selectedId !== null && (detailState?.loading ?? true)

  useEffect(() => {
    let isMounted = true
    const fetchAll = async () => {
      if (isMounted) await load()
    }
    void fetchAll()
    return () => { isMounted = false }
  }, [load])

  // Flat list for selection
  const allDocs = useMemo(() => topics.flatMap((t) => t.documents), [topics])
  const selectedListItem = selectedDoc

  const handleSelect = (doc: DocListItem) => {
    setCreateMode(false)
    // Selecting is a one-way action: clicking the already-open card keeps its
    // detail in place. Closing is the panel's explicit X button (or the narrow
    // dialog's dismiss), never a second click on the card — otherwise a stray
    // click on the row you're reading makes the detail vanish.
    setSelectedDoc(doc)
  }

  const handleNewDoc = () => {
    setSelectedDoc(null)
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
      setSelectedDoc(selectedDoc?.id === target.id ? null : selectedDoc)
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
      onClose={() => { setSelectedDoc(null); setCreateMode(false) }}
      onSaveEdit={handleSaveEdit}
      onCreate={handleCreate}
      onDelete={handleDelete}
      onPromote={handlePromote}
      onAddRelation={async () => {}}
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
        <span className="hidden sm:inline-block text-xs text-muted-foreground font-medium">
          {matching} of {total}
        </span>

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
        <div className={mainColClass}>
          {error && (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
              {error}
            </div>
          )}

          {/* Toolbar: filters */}
          <div className="sticky top-0 z-10 flex flex-col gap-2 bg-background pb-2 pt-4">
            <div className="flex flex-wrap items-center gap-2">
              <div className={`relative min-w-[200px] flex-1 ${panelOpen ? "basis-full" : ""}`}>
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-4 text-muted-foreground" />
                <Input
                  placeholder="Filter by topic..."
                  value={topic ?? ""}
                  onChange={(e) => { setOffset(0); setTopic(e.target.value) }}
                  className="pl-9"
                />
              </div>

              <Select value={kind} onValueChange={(v) => { setOffset(0); setKind(v as KindFilter) }}>
                <SelectTrigger className="w-[130px]">
                  <SelectValue placeholder="Kind" />
                </SelectTrigger>
                <SelectContent>
                  {docKinds().map((k) => (
                    <SelectItem key={k} value={k}>{docKindLabel(k)}</SelectItem>
                  ))}
                </SelectContent>
              </Select>

              <Select value={ownership} onValueChange={(v) => { setOffset(0); setOwnership(v as OwnershipFilter) }}>
                <SelectTrigger className="w-[140px]">
                  <SelectValue placeholder="Ownership" />
                </SelectTrigger>
                <SelectContent>
                  {docOwnerships().map((o) => (
                    <SelectItem key={o} value={o}>{docOwnershipLabel(o)}</SelectItem>
                  ))}
                </SelectContent>
              </Select>

              <Select value={origin} onValueChange={(v) => { setOffset(0); setOrigin(v as OriginFilter) }}>
                <SelectTrigger className="w-[140px]">
                  <SelectValue placeholder="Origin" />
                </SelectTrigger>
                <SelectContent>
                  {docOrigins().map((o) => (
                    <SelectItem key={o} value={o}>{docOriginLabel(o)}</SelectItem>
                  ))}
                </SelectContent>
              </Select>

              {availableTopics.length > 0 && (
                <Select value={topic ?? ""} onValueChange={(v) => { setOffset(0); setTopic(v) }}>
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
                    selected={selectedDoc?.id === doc.id}
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
                  setSelectedDoc(null)
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
              <span className="font-mono">{deleteTarget.relPath}</span>
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