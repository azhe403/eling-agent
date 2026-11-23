"use client"

import * as React from "react"
import { useCallback, useState } from "react"
import {
  ChevronDown,
  ChevronRight,
  ExternalLink,
  AlertCircle,
  Clock,
  Link2,
  X,
} from "lucide-react"
import { formatDistanceToNowStrict } from "date-fns"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { apiFetch } from "@/lib/api"
import type {
  DocListItem,
  DocReadResult,
  DocSection,
  DocRef,
  DocRelation,
  DocKindDto,
  DocOwnershipDto,
  DocOriginDto,
  ReferenceKindDto,
  RelationKindDto,
} from "@/lib/types"

interface DocDetailPanelProps {
  variant: "aside" | "stacked"
  summary: DocListItem | null
  detail: DocReadResult | null
  detailLoading: boolean
  detailError: string | null
  createMode: boolean
  trashMode: boolean
  onClose: () => void
  onSaveEdit: (id: string, data: DocFormData) => Promise<void>
  onCreate: (data: DocFormData) => Promise<void>
  onDelete: (id: string) => void
  onPromote: (id: string) => void
  onAddRelation: (sourceId: string, targetId: string, kind: RelationKindDto) => Promise<void>
}

export type DocFormData = {
  relPath: string
  content: string
  kind: DocKindDto
  topic: string
  status: number
  order: number
}

const KIND_LABELS: Record<DocKindDto, string> = {
  0: "Spec",
  1: "Plan",
  2: "Doc",
  3: "Scratch",
}

const KIND_COLORS: Record<DocKindDto, string> = {
  0: "bg-blue-500/10 text-blue-600 dark:text-blue-400 border-blue-500/20",
  1: "bg-purple-500/10 text-purple-600 dark:text-purple-400 border-purple-500/20",
  2: "bg-green-500/10 text-green-600 dark:text-green-400 border-green-500/20",
  3: "bg-gray-500/10 text-gray-600 dark:text-gray-400 border-gray-500/20",
}

const OWNERSHIP_LABELS: Record<DocOwnershipDto, string> = {
  0: "Managed",
  1: "External",
}

const ORIGIN_LABELS: Record<DocOriginDto, string> = {
  0: "Current",
  1: "Ancestor",
  2: "Shard",
  3: "Global",
}

const REF_KIND_LABELS: Record<ReferenceKindDto, string> = {
  0: "Symbol",
  1: "Path",
  2: "Anchor Link",
}

const REL_KIND_LABELS: Record<RelationKindDto, string> = {
  0: "Implements",
  1: "Supersedes",
  2: "References",
}

const REL_KIND_COLORS: Record<RelationKindDto, string> = {
  0: "bg-blue-500/10 text-blue-600 dark:text-blue-400",
  1: "bg-amber-500/10 text-amber-600 dark:text-amber-400",
  2: "bg-green-500/10 text-green-600 dark:text-green-400",
}

function SpecStatusLabel(kind: DocKindDto, status: number): string {
  if (kind === 0) {
    return ["Proposed", "Approved", "Rejected", "Superseded"][status] ?? `Status ${status}`
  }
  if (kind === 1) {
    return ["Draft", "Active", "Completed", "Superseded"][status] ?? `Status ${status}`
  }
  return ["Active", "Archived", "Superseded"][status] ?? `Status ${status}`
}

function SpecStatusColor(status: number): string {
  if (status === 0) return "bg-amber-500/10 text-amber-600 dark:text-amber-400"
  if (status === 1) return "bg-green-500/10 text-green-600 dark:text-green-400"
  if (status === 2) return "bg-destructive/10 text-destructive"
  if (status === 3) return "bg-gray-500/10 text-gray-600 dark:text-gray-400"
  return "bg-gray-500/10 text-gray-600"
}

// Inline Tabs implementation.
//
// The previous version injected the active tab through React.cloneElement,
// which cannot work: the child destructures `value` as its OWN prop (the tab's
// identity), so the injected active value overwrote it and both sides compared
// undefined — every trigger read unselected and no panel ever rendered. A
// context carries the active value beside the identity instead of over it.
const TabsContext = React.createContext<{ value: string; setValue: (v: string) => void } | null>(null)

function Tabs({ defaultValue, className, children }: { defaultValue: string; className?: string; children: React.ReactNode }) {
  const [value, setValue] = useState(defaultValue)
  return (
    <TabsContext.Provider value={{ value, setValue }}>
      <div className={className}>{children}</div>
    </TabsContext.Provider>
  )
}

function TabsList({ className, children }: { className?: string; children: React.ReactNode }) {
  return <div className={className} role="tablist">{children}</div>
}

function TabsTrigger({
  value: triggerValue,
  className,
  children,
}: { value: string; className?: string; children: React.ReactNode }) {
  const ctx = React.useContext(TabsContext)
  const selected = ctx?.value === triggerValue
  return (
    <button
      role="tab"
      aria-selected={selected}
      onClick={() => ctx?.setValue(triggerValue)}
      className={`-mb-px border-b-2 px-2 py-2.5 text-xs font-medium transition-colors ${
        selected
          ? "border-primary text-foreground"
          : "border-transparent text-muted-foreground hover:text-foreground"
      } ${className ?? ""}`}
    >
      {children}
    </button>
  )
}

function TabsContent({
  value: contentValue,
  className,
  children,
}: { value: string; className?: string; children: React.ReactNode }) {
  const ctx = React.useContext(TabsContext)
  if (ctx?.value !== contentValue) return null
  return <div className={className} role="tabpanel">{children}</div>
}

// Simple scroll area
const ScrollArea = ({ className, children }: { className?: string; children: React.ReactNode }) => (
  <div className={`overflow-auto ${className ?? ""}`}>{children}</div>
)

export function DocDetailPanel({
  variant,
  summary,
  detail,
  detailLoading,
  detailError,
  createMode,
  trashMode,
  onClose,
  onSaveEdit,
  onCreate,
  onDelete,
  onPromote,
  onAddRelation,
}: DocDetailPanelProps) {
  const [showRelationDialog, setShowRelationDialog] = useState(false)
  const [relationSourceId, setRelationSourceId] = useState<string>("")
  const [relationTargetId, setRelationTargetId] = useState<string>("")
  const [relationKind, setRelationKind] = useState<RelationKindDto>(0)
  const [relationError, setRelationError] = useState<string | null>(null)
  const [relationLoading, setRelationLoading] = useState(false)

  // Must run unconditionally: every early return below (no doc, create mode,
  // trashed, error, loading) would otherwise skip this hook and change the
  // hook order once the detail finishes loading.
  const handleAddRelation = useCallback(async () => {
    if (!relationSourceId || !relationTargetId) {
      setRelationError("Both source and target IDs are required")
      return
    }
    setRelationLoading(true)
    setRelationError(null)
    try {
      await onAddRelation(relationSourceId, relationTargetId, relationKind)
      setShowRelationDialog(false)
      setRelationSourceId("")
      setRelationTargetId("")
    } catch (e) {
      setRelationError(e instanceof Error ? e.message : "Failed to add relation")
    } finally {
      setRelationLoading(false)
    }
  }, [onAddRelation, relationSourceId, relationTargetId, relationKind])

  const doc = detail?.document ?? summary
  const isTrashed = trashMode && summary && !detail

  if (!doc && !createMode) return null

  // Handle create mode
  if (createMode) {
    return (
      <DocEditor
        variant={variant}
        initialData={{
          relPath: "",
          content: "",
          kind: 2,
          topic: "",
          status: 0,
          order: 0,
        }}
        onSave={onCreate}
        onCancel={onClose}
        isLoading={detailLoading}
      />
    )
  }

  // Trashed view
  if (isTrashed && summary) {
    return (
      <div className="flex flex-col gap-4 h-full">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-medium">Trashed Document</h2>
          <Button variant="ghost" size="sm" onClick={onClose}>
            <X className="size-4" />
          </Button>
        </div>
        <div className="rounded-lg border border-destructive/20 bg-destructive/5 p-4 text-sm text-destructive">
          This document is in the trash. Restore it to view details.
        </div>
        <Button variant="outline" onClick={() => {}} disabled>
          Restore (not implemented)
        </Button>
      </div>
    )
  }

  // Error view
  if (detailError) {
    return (
      <div className="flex flex-col gap-4 h-full">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-medium">{doc?.title ?? "Document"}</h2>
          <Button variant="ghost" size="sm" onClick={onClose}>
            <X className="size-4" />
          </Button>
        </div>
        <div className="rounded-lg border border-destructive/20 bg-destructive/5 p-4 text-sm text-destructive">
          {detailError}
        </div>
      </div>
    )
  }

  // Loading view
  if (detailLoading || !detail) {
    return (
      <div className="flex flex-col gap-4 h-full">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-medium animate-pulse">Loading...</h2>
          <Button variant="ghost" size="sm" onClick={onClose}>
            <X className="size-4" />
          </Button>
        </div>
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-8 bg-muted animate-pulse rounded" />
          ))}
        </div>
      </div>
    )
  }

  // At this point, doc is guaranteed to exist
  const currentDoc = doc as DocListItem
  const kindLabel = KIND_LABELS[currentDoc.kind as DocKindDto] ?? `Kind ${currentDoc.kind}`
  const kindColor = KIND_COLORS[currentDoc.kind as DocKindDto] ?? "bg-gray-500/10 text-gray-600"
  const ownershipLabel = OWNERSHIP_LABELS[currentDoc.ownership as DocOwnershipDto] ?? `Ownership ${currentDoc.ownership}`
  const originLabel = ORIGIN_LABELS[currentDoc.origin as DocOriginDto] ?? `Origin ${currentDoc.origin}`

  let timeAgo = ""
  if (currentDoc.mTime) {
    const parsed = new Date(currentDoc.mTime)
    if (!Number.isNaN(parsed.getTime())) {
      try {
        timeAgo = formatDistanceToNowStrict(parsed, { addSuffix: true })
      } catch {
        timeAgo = ""
      }
    }
  }

  const sections = detail.sections ?? []
  const refs = detail.references ?? []
  const relations = detail.relations ?? []

  // Header section
  function renderHeader() {
    return (
      <div className="flex flex-col gap-3">
        <div className="flex items-start justify-between gap-2">
          <div className="flex-1 min-w-0">
            <div className="mb-2 flex flex-wrap items-center gap-1.5">
              <Badge variant="outline" className={`text-xs ${kindColor}`}>
                {kindLabel}
              </Badge>
              <Badge variant="outline" className="text-xs bg-muted/50">
                {ownershipLabel}
              </Badge>
              <Badge variant="outline" className="text-xs bg-muted/50">
                {originLabel}
              </Badge>
              <Badge variant="outline" className={`text-xs ${SpecStatusColor(currentDoc.status)}`}>
                {SpecStatusLabel(currentDoc.kind as DocKindDto, currentDoc.status)}
              </Badge>
            </div>
            <h2 className="truncate text-base font-semibold leading-snug">{currentDoc.title}</h2>
            <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
              <span className="max-w-[220px] truncate font-mono">{currentDoc.relPath}</span>
              <span aria-hidden>·</span>
              <span className="font-mono">ID: {currentDoc.id.slice(0, 8)}…</span>
              {timeAgo && (
                <>
                  <span aria-hidden>·</span>
                  <span>{timeAgo}</span>
                </>
              )}
            </div>
          </div>
          <Button
            variant="ghost"
            size="icon"
            onClick={onClose}
            aria-label="Close document detail"
            className="size-7 shrink-0 text-muted-foreground hover:text-foreground"
          >
            <X className="size-4" />
          </Button>
        </div>

        {currentDoc.topic && (
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <span>Topic</span>
            <Badge variant="outline" className="text-xs">{currentDoc.topic}</Badge>
          </div>
        )}

        {currentDoc.supersededBy && (
          <div className="flex items-center gap-2 text-xs text-amber-600 dark:text-amber-400">
            <ChevronRight className="size-3 shrink-0" />
            <span>Superseded by <code className="font-mono">{currentDoc.supersededBy}</code></span>
          </div>
        )}

        {currentDoc.frontMatterError && (
          <div className="flex items-start gap-2 text-xs text-destructive">
            <AlertCircle className="mt-0.5 size-3.5 shrink-0" />
            <span>Front-matter error: {currentDoc.frontMatterErrorMessage}</span>
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2 pt-1">
          <Button variant="outline" size="sm" onClick={() => onDelete(currentDoc.id)}>
            Delete
          </Button>
          {currentDoc.ownership === 0 && currentDoc.origin === 0 && (
            <Button variant="outline" size="sm" onClick={() => onPromote(currentDoc.id)}>
              Promote to docs/
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={() => {
            setRelationSourceId(currentDoc.id)
            setShowRelationDialog(true)
          }}>
            <Link2 className="size-4 mr-1" />
            Add Relation
          </Button>
        </div>
      </div>
    )
  }

  // Content tab: the document's raw markdown, so clicking a card actually lets
  // the reader read the document. Rendered as monospace text — the dashboard
  // ships no markdown renderer, and showing the honest source beats a half
  // rendering that silently drops tables and links.
  function renderContentTab() {
    const content = detail?.content

    if (content === undefined || content === null) {
      return (
        <ScrollArea className="h-full">
          <div className="px-4 py-12 text-center text-xs text-muted-foreground">
            Content not available for this document.
          </div>
        </ScrollArea>
      )
    }

    if (content.trim().length === 0) {
      return (
        <ScrollArea className="h-full">
          <div className="px-4 py-12 text-center text-xs text-muted-foreground">
            This document is empty.
          </div>
        </ScrollArea>
      )
    }

    return (
      <ScrollArea className="h-full">
        <pre className="whitespace-pre-wrap break-words p-4 text-xs leading-relaxed font-mono text-foreground">
          {content}
        </pre>
      </ScrollArea>
    )
  }

  // Sections tab content
  function renderSectionsTab() {
    return (
      <ScrollArea className="h-full">
        <div className="flex flex-col gap-2 p-4">
          {sections.length === 0 ? (
            <div className="px-4 py-12 text-center text-xs text-muted-foreground">
              No sections found
            </div>
          ) : (
            sections.map((section) => (
              <Collapsible key={section.anchor} defaultOpen={section.level <= 2}>
                <CollapsibleTrigger className="w-full text-left p-2 hover:bg-accent rounded transition-colors">
                  <div className="flex items-center gap-2">
                    <Badge variant="outline" className={`text-[10px] ${SpecStatusColor(section.status)}`}>
                      {SpecStatusLabel(currentDoc.kind as DocKindDto, section.status)}
                    </Badge>
                    <span className="font-medium flex-1 truncate">
                      {"  ".repeat(Math.max(0, section.level - 1))}{section.heading}
                    </span>
                    <span className="text-xs text-muted-foreground font-mono">
                      L{section.lineStart}–L{section.lineEnd}
                    </span>
                    {section.stepsTotal > 0 && (
                      <Badge variant="outline" className="text-[10px] bg-muted/50">
                        {section.stepsDone}/{section.stepsTotal}
                      </Badge>
                    )}
                  </div>
                </CollapsibleTrigger>
                <CollapsibleContent className="pt-2 pl-6 border-l border-muted/50">
                  {section.annotation && (
                    <div className="rounded bg-muted/50 p-3 text-sm text-muted-foreground mb-2">
                      <span className="font-medium text-foreground">Annotation:</span>{" "}
                      {section.annotation}
                    </div>
                  )}
                  <div className="text-xs text-muted-foreground font-mono">
                    Anchor: {section.anchor}
                  </div>
                </CollapsibleContent>
              </Collapsible>
            ))
          )}
        </div>
      </ScrollArea>
    )
  }

  // Refs tab content
  function renderRefsTab() {
    return (
      <ScrollArea className="h-full">
        <div className="flex flex-col gap-2 p-4">
          {refs.length === 0 ? (
            <div className="px-4 py-12 text-center text-xs text-muted-foreground">
              No references found
            </div>
          ) : (
            refs.map((ref, index) => {
              const refKey = `${ref.anchor}-${index}`
              return (
                <Collapsible key={refKey} defaultOpen={false}>
                  <CollapsibleTrigger className="w-full text-left p-3 rounded border bg-muted/30 hover:bg-muted/50 transition-colors">
                    <div className="flex items-center gap-2 mb-1">
                      <Badge variant="outline" className="text-[10px] bg-muted/50">
                        {REF_KIND_LABELS[ref.refKind] ?? `Kind ${ref.refKind}`}
                      </Badge>
                      <span className="font-mono text-sm truncate flex-1">{ref.rawText}</span>
                      <Badge
                        variant="outline"
                        className={`text-[10px] ${ref.resolved ? "bg-green-500/10 text-green-600" : "bg-destructive/10 text-destructive"}`}
                      >
                        {ref.resolved ? "Resolved" : "Unresolved"}
                      </Badge>
                    </div>
                    {ref.resolved && ref.resolvedPath && (
                      <div className="text-xs text-muted-foreground font-mono truncate">
                        {ref.resolvedPath}:{ref.resolvedLine ?? "?"}
                      </div>
                    )}
                  </CollapsibleTrigger>
                  <CollapsibleContent className="pt-2">
                    {ref.resolved && ref.resolvedPath && (
                      <div className="space-y-1 text-xs">
                        <div className="flex items-center gap-2 text-muted-foreground">
                          <ExternalLink className="size-3" />
                          <span className="font-mono">{ref.resolvedPath}</span>
                          {ref.resolvedLine && (
                            <span>
                              :<span className="font-mono">{ref.resolvedLine}</span>
                            </span>
                          )}
                        </div>
                        <div className="text-muted-foreground">
                          Resolved at: {new Date(ref.resolvedAt!).toLocaleString()}
                        </div>
                      </div>
                    )}
                    {!ref.resolved && (
                      <div className="text-xs text-destructive">
                        This reference could not be resolved against the codebase index.
                      </div>
                    )}
                  </CollapsibleContent>
                </Collapsible>
              )
            })
          )}
        </div>
      </ScrollArea>
    )
  }

  // Relations tab content
  function renderRelationsTab() {
    return (
      <ScrollArea className="h-full">
        <div className="flex flex-col gap-2 p-4">
          {relations.length > 0 ? (
            relations.map((rel: DocRelation, index: number) => {
              const relKey = `${rel.sourceId}-${rel.targetId}-${index}`
              return (
                <div key={relKey} className="rounded border bg-muted/30 p-3">
                  <div className="flex items-center gap-2 mb-2">
                    <Badge
                      variant="outline"
                      className={`text-sm ${REL_KIND_COLORS[rel.kind]}`}
                    >
                      {REL_KIND_LABELS[rel.kind] ?? `Kind ${rel.kind}`}
                    </Badge>
                    <span className="font-mono text-sm">{rel.sourceId.slice(0, 8)}</span>
                    <ChevronRight className="size-4 text-muted-foreground" />
                    <span className="font-mono text-sm">{rel.targetId.slice(0, 8)}</span>
                  </div>
                </div>
              )
            })
          ) : (
            <div className="px-4 py-12 text-center text-xs text-muted-foreground">
              No relations found
            </div>
          )}
        </div>
      </ScrollArea>
    )
  }

  // Tabs
  function renderTabs() {
    return (
      <Tabs defaultValue="content" className="flex min-h-0 flex-1 flex-col">
        <TabsList className="grid w-full shrink-0 grid-cols-4 border-b border-border/40 bg-background">
          <TabsTrigger value="content">Content</TabsTrigger>
          <TabsTrigger value="sections">Sections ({sections.length})</TabsTrigger>
          <TabsTrigger value="refs">References ({refs.length})</TabsTrigger>
          <TabsTrigger value="relations">Relations ({relations.length})</TabsTrigger>
        </TabsList>
        <TabsContent value="content" className="flex min-h-0 flex-1 flex-col">{renderContentTab()}</TabsContent>
        <TabsContent value="sections" className="flex min-h-0 flex-1 flex-col">{renderSectionsTab()}</TabsContent>
        <TabsContent value="refs" className="flex min-h-0 flex-1 flex-col">{renderRefsTab()}</TabsContent>
        <TabsContent value="relations" className="flex min-h-0 flex-1 flex-col">{renderRelationsTab()}</TabsContent>
      </Tabs>
    )
  }

  // Relation dialog
  function renderRelationDialog() {
    return (
      <Dialog open={showRelationDialog} onOpenChange={setShowRelationDialog}>
        <DialogContent className="sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>Add Document Relation</DialogTitle>
            <DialogDescription>
              Create a relation from the current document to another document.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-4">
            <div>
              <label className="text-sm font-medium mb-1 block">Relation Type</label>
              <select
                value={relationKind}
                onChange={(e) => setRelationKind(Number(e.target.value) as RelationKindDto)}
                className="w-full rounded border bg-background px-3 py-2 text-sm"
              >
                <option value={0}>Implements (Plan → Spec)</option>
                <option value={1}>Supersedes (same kind)</option>
                <option value={2}>References (any)</option>
              </select>
            </div>
            <div>
              <label className="text-sm font-medium mb-1 block">Source Document ID</label>
              <input
                value={relationSourceId}
                onChange={(e) => setRelationSourceId(e.target.value)}
                className="w-full rounded border bg-background px-3 py-2 text-sm font-mono"
                placeholder="Auto-filled from current doc"
              />
            </div>
            <div>
              <label className="text-sm font-medium mb-1 block">Target Document ID</label>
              <input
                value={relationTargetId}
                onChange={(e) => setRelationTargetId(e.target.value)}
                className="w-full rounded border bg-background px-3 py-2 text-sm font-mono"
                placeholder="Enter target document ID"
              />
            </div>
            {relationError && (
              <div className="text-sm text-destructive">{relationError}</div>
            )}
          </div>
          <div className="flex justify-end gap-2 border-t pt-4">
            <Button variant="outline" onClick={() => setShowRelationDialog(false)}>
              Cancel
            </Button>
            <Button onClick={handleAddRelation} disabled={relationLoading}>
              {relationLoading ? "Adding..." : "Add Relation"}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    )
  }

  // Stacked variant: the caller owns the dialog chrome (see DocsList), so this
  // renders the panel body only — the same split the skills page uses.
  if (variant === "stacked") {
    return (
      <div className="flex h-full min-h-0 flex-col bg-background">
        <div className="flex shrink-0 flex-col gap-3 border-b border-border/40 p-4">
          {renderHeader()}
        </div>
        <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
          {renderTabs()}
        </div>
        {renderRelationDialog()}
      </div>
    )
  }

  // Aside variant: docked right-hand column on wide screens.
  return (
    <aside
      aria-label="Document detail"
      className="flex h-full min-h-0 min-w-[380px] flex-1 shrink-0 flex-col border-l bg-background"
    >
      <div className="flex shrink-0 flex-col gap-3 border-b border-border/40 p-4">
        {renderHeader()}
      </div>
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        {renderTabs()}
      </div>
      {renderRelationDialog()}
    </aside>
  )
}

// Inline editor component for create/edit
function DocEditor({
  variant,
  initialData,
  onSave,
  onCancel,
  isLoading,
}: {
  variant: "aside" | "stacked"
  initialData: DocFormData
  onSave: (data: DocFormData) => Promise<void>
  onCancel: () => void
  isLoading: boolean
}) {
  const [formData, setFormData] = useState<DocFormData>(initialData)
  const [error, setError] = useState<string | null>(null)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    try {
      await onSave(formData)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Save failed")
    }
  }

  const editor = (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4 h-full min-h-0">
      <div className="space-y-2">
        <label className="text-sm font-medium">Relative Path <span className="text-destructive">*</span></label>
        <input
          value={formData.relPath}
          onChange={(e) => setFormData({ ...formData, relPath: e.target.value })}
          className="w-full rounded border bg-background px-3 py-2 text-sm font-mono"
          placeholder=".eling/docs/specs/my-spec.md"
          required
        />
      </div>
      <div className="space-y-2">
        <label className="text-sm font-medium">Kind</label>
        <select
          value={formData.kind}
          onChange={(e) => setFormData({ ...formData, kind: Number(e.target.value) as DocKindDto })}
          className="w-full rounded border bg-background px-3 py-2 text-sm"
        >
          <option value={0}>Spec</option>
          <option value={1}>Plan</option>
          <option value={2}>Doc</option>
          <option value={3}>Scratch</option>
        </select>
      </div>
      <div className="space-y-2">
        <label className="text-sm font-medium">Topic</label>
        <input
          value={formData.topic}
          onChange={(e) => setFormData({ ...formData, topic: e.target.value })}
          className="w-full rounded border bg-background px-3 py-2 text-sm"
          placeholder="e.g., superpowers, billing, etc."
        />
      </div>
      <div className="space-y-2">
        <label className="text-sm font-medium">Status</label>
        <input
          type="number"
          value={formData.status}
          onChange={(e) => setFormData({ ...formData, status: Number(e.target.value) })}
          className="w-full rounded border bg-background px-3 py-2 text-sm"
          min={0}
        />
      </div>
      <div className="space-y-2">
        <label className="text-sm font-medium">Order</label>
        <input
          type="number"
          value={formData.order}
          onChange={(e) => setFormData({ ...formData, order: Number(e.target.value) })}
          className="w-full rounded border bg-background px-3 py-2 text-sm"
          min={0}
        />
      </div>
      <div className="space-y-2 flex-1 min-h-0">
        <label className="text-sm font-medium">Content (Markdown) <span className="text-destructive">*</span></label>
        <textarea
          value={formData.content}
          onChange={(e) => setFormData({ ...formData, content: e.target.value })}
          className="w-full h-full min-h-[200px] rounded border bg-background px-3 py-2 text-sm font-mono resize-none"
          placeholder="# Title\n\nContent here..."
          required
        />
      </div>
      {error && <div className="text-sm text-destructive">{error}</div>}
      <div className="flex justify-end gap-2 border-t pt-4">
        <Button type="button" variant="outline" onClick={onCancel} disabled={isLoading}>
          Cancel
        </Button>
        <Button type="submit" disabled={isLoading || !formData.relPath.trim() || !formData.content.trim()}>
          {isLoading ? "Saving..." : "Save"}
        </Button>
      </div>
    </form>
  )

  if (variant === "stacked") {
    return (
      <Dialog open onOpenChange={onCancel}>
        <DialogContent className="max-h-[85svh] w-[94vw] overflow-hidden p-0 sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>{initialData.relPath ? "Edit Document" : "New Document"}</DialogTitle>
          </DialogHeader>
          {editor}
        </DialogContent>
      </Dialog>
    )
  }

  return <div className="flex flex-col h-full p-4">{editor}</div>
}