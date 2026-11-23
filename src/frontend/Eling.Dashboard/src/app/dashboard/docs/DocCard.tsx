"use client"

import { ChevronRight, FileText, AlertCircle, CheckCircle2, Clock } from "lucide-react"
import { formatDistanceToNowStrict } from "date-fns"

import { cn } from "cn"
import { Badge } from "@/components/ui/badge"
import type { DocListItem, DocKindDto, DocOwnershipDto, DocOriginDto } from "@/lib/types"

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

const STATUS_LABELS: Record<number, { label: string; icon: React.ReactNode; color: string }> = {
  // Spec statuses: 0=Proposed, 1=Approved, 2=Rejected, 3=Superseded
  // Plan statuses: 0=Draft, 1=Active, 2=Completed, 3=Superseded
  // Doc/Scratch: 0=Active, 1=Archived, 2=Superseded
  0: { label: "Proposed/Draft/Active", icon: <Clock className="size-3" />, color: "bg-amber-500/10 text-amber-600 dark:text-amber-400" },
  1: { label: "Approved/Active/Archived", icon: <CheckCircle2 className="size-3" />, color: "bg-green-500/10 text-green-600 dark:text-green-400" },
  2: { label: "Rejected/Completed/Superseded", icon: <AlertCircle className="size-3" />, color: "bg-destructive/10 text-destructive" },
  3: { label: "Superseded", icon: <ChevronRight className="size-3" />, color: "bg-gray-500/10 text-gray-600 dark:text-gray-400" },
}

export function DocCard({ doc, selected, onSelect }: DocCardProps) {
  const kindLabel = KIND_LABELS[doc.kind as DocKindDto] ?? `Kind ${doc.kind}`
  const kindColor = KIND_COLORS[doc.kind as DocKindDto] ?? "bg-gray-500/10 text-gray-600"
  const ownershipLabel = OWNERSHIP_LABELS[doc.ownership as DocOwnershipDto] ?? `Ownership ${doc.ownership}`
  const originLabel = ORIGIN_LABELS[doc.origin as DocOriginDto] ?? `Origin ${doc.origin}`
  const statusInfo = STATUS_LABELS[doc.status] ?? { label: `Status ${doc.status}`, icon: <FileText className="size-3" />, color: "bg-gray-500/10 text-gray-600" }

  // The list endpoint sends no mTime, so most cards have nothing to format.
  // Returning "" (instead of "unknown") lets the meta row drop the separator
  // and the word entirely rather than printing "· unknown" on every card.
  let timeAgo = ""
  if (doc.mTime) {
    const parsed = new Date(doc.mTime)
    if (!Number.isNaN(parsed.getTime())) {
      try {
        timeAgo = formatDistanceToNowStrict(parsed, { addSuffix: true })
      } catch {
        timeAgo = ""
      }
    }
  }

  const hasSteps = doc.stepsTotal > 0
  const progressPct = hasSteps ? Math.round((doc.stepsDone / doc.stepsTotal) * 100) : 0

  return (
    <div
      role="button"
      tabIndex={0}
      onClick={() => onSelect(doc)}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault()
          onSelect(doc)
        }
      }}
      className={cn(
        "group relative flex flex-col rounded-lg border p-3 text-sm cursor-pointer transition-colors",
        "hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected
          ? "border-foreground/30 bg-primary/5"
          : "border-border",
      )}
    >
      <div className="p-0">
        <div className="flex items-start justify-between gap-2">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 flex-wrap mb-2">
              <Badge
                variant="outline"
                className={`text-xs ${kindColor}`}
              >
                {kindLabel}
              </Badge>
              <Badge variant="outline" className="text-xs bg-muted/50">
                {ownershipLabel}
              </Badge>
              <Badge variant="outline" className="text-xs bg-muted/50">
                {originLabel}
              </Badge>
            </div>

            <h3 className="font-medium text-sm truncate text-foreground group-hover:text-primary transition-colors">
              {doc.title}
            </h3>

            <div className="mt-1.5 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[11px] text-muted-foreground">
              <span className="max-w-[240px] truncate font-mono">{doc.relPath}</span>
              {timeAgo && (
                <>
                  <span aria-hidden>·</span>
                  <span>{timeAgo}</span>
                </>
              )}
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

          <div className="flex flex-col items-end gap-1 shrink-0">
            <Badge variant="outline" className={`text-[10px] ${statusInfo.color}`}>
              {statusInfo.icon}
              <span className="hidden sm:inline">{statusInfo.label}</span>
            </Badge>
            {doc.order > 0 && (
              <span className="text-[10px] text-muted-foreground font-mono">order: {doc.order}</span>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}