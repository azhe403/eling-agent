"use client"

import { ChevronRight, FileText, AlertCircle, CheckCircle2, Clock } from "lucide-react"

import { RelativeTime } from "@/components/relative-time"
import { cn } from "@/lib/utils"
import { Badge } from "@/components/ui/badge"
import type { DocListItem, DocKindDto } from "@/lib/types"
import { docStatusLabel } from "./docs-scope"

interface DocCardProps {
  doc: DocListItem
  selected: boolean
  onSelect: (doc: DocListItem) => void
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

const STATUS_STYLE: Record<number, { icon: React.ReactNode; color: string }> = {
  0: { icon: <Clock className="size-3" />, color: "bg-amber-500/10 text-amber-600 dark:text-amber-400" },
  1: { icon: <CheckCircle2 className="size-3" />, color: "bg-green-500/10 text-green-600 dark:text-green-400" },
  2: { icon: <AlertCircle className="size-3" />, color: "bg-destructive/10 text-destructive" },
  3: { icon: <ChevronRight className="size-3" />, color: "bg-gray-500/10 text-gray-600 dark:text-gray-400" },
}

export function DocCard({ doc, selected, onSelect }: DocCardProps) {
  const kindLabel = KIND_LABELS[doc.kind as DocKindDto] ?? `Kind ${doc.kind}`
  const kindColor = KIND_COLORS[doc.kind as DocKindDto] ?? "bg-gray-500/10 text-gray-600"
  const statusStyle = STATUS_STYLE[doc.status] ?? { icon: <FileText className="size-3" />, color: "bg-gray-500/10 text-gray-600" }
  const statusLabel = docStatusLabel(doc.kind as DocKindDto, doc.status)

  // Rows with no usable mTime drop the badge entirely instead of showing
  // "unknown"; the label itself refreshes on a shared clock.
  const docTime = doc.mTime && !Number.isNaN(Date.parse(doc.mTime)) ? doc.mTime : null

  const hasSteps = doc.stepsTotal > 0
  const progressPct = hasSteps ? Math.round((doc.stepsDone / doc.stepsTotal) * 100) : 0

  return (
    <button
      type="button"
      aria-pressed={selected}
      aria-label={`View document ${doc.title}`}
      onClick={() => onSelect(doc)}
      className={cn(
        "group relative flex w-full flex-col rounded-lg border p-3 text-start text-sm cursor-pointer transition-colors",
        "hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected
          ? "border-foreground/30 bg-primary/5 ring-1 ring-foreground/30"
          : "border-border",
      )}
    >
      <div className="min-w-0">
        {/* Status sits in the badge row so it no longer reserves width for the
            whole card; the title and meta row get the full card width. */}
        <div className="flex items-center gap-2 flex-wrap mb-2">
          <Badge
            variant="outline"
            className={`text-xs ${kindColor}`}
          >
            {kindLabel}
          </Badge>
          {docTime && (
            <Badge variant="outline" className="text-xs bg-muted/50" title={`workspace: ${doc.workspaceId}`}>
              <Clock className="size-3" />
              <RelativeTime value={docTime} />
            </Badge>
          )}
          {doc.archived && (
            <Badge variant="outline" className="text-xs bg-muted/50">Archived</Badge>
          )}
          <div className="ml-auto flex items-center gap-1.5">
            <Badge variant="outline" className={`text-[10px] ${statusStyle.color}`}>
              {statusStyle.icon}
              <span className="hidden sm:inline">{statusLabel}</span>
            </Badge>
            {doc.order > 0 && (
              <span className="text-[10px] text-muted-foreground font-mono">order: {doc.order}</span>
            )}
          </div>
        </div>

        <h3 className="font-medium text-sm truncate text-foreground group-hover:text-primary transition-colors">
          {doc.title}
        </h3>

        <div className="mt-1.5 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[11px] text-muted-foreground">
          <span className="max-w-[240px] truncate font-mono">{doc.path}</span>
          {hasSteps && (
            <>
              <span aria-hidden>·</span>
              <span>{doc.stepsDone}/{doc.stepsTotal} steps</span>
            </>
          )}
        </div>

        {hasSteps && (
          <div className="mt-2 h-1.5 bg-muted rounded-full overflow-hidden">
            <div
              className="h-full bg-primary transition-all duration-300"
              style={{ width: `${progressPct}%` }}
            />
          </div>
        )}

        {doc.frontMatterError && (
          <div className="mt-2 flex items-center gap-1.5 text-xs text-destructive">
            <AlertCircle className="size-3" />
            <span>Front-matter error: {doc.frontMatterErrorMessage}</span>
          </div>
        )}

        {doc.supersededBy && (
          <div className="mt-2 flex items-center gap-1.5 text-xs text-amber-600 dark:text-amber-400">
            <ChevronRight className="size-3" />
            <span>Superseded by: {doc.supersededBy}</span>
          </div>
        )}
      </div>
    </button>
  )
}