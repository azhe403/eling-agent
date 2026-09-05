"use client"

import { Check, Copy, Pencil, Trash2 } from "lucide-react"

import { Button } from "@/components/ui/button"
import { formatDate } from "@/lib/date-utils"
import { statusBadge, typeBadge } from "@/lib/types"
import type { Memory, Runtime } from "@/lib/types"

export type MemoryCardProps = {
  memory: Memory
  copiedId: string | null
  runtimes: Runtime[]
  onCopy: (text: string, id: string) => void
  onEdit: (id: string) => void
  onDelete: (m: Memory) => void
  onPromote: (m: Memory) => void
  onCopyToProject: (m: Memory, projectRoot: string) => void
}

// Renders one memory card. Extracted from the original god component so the
// parent can stay focused on list state, fetching and dialogs. Visual structure
// is preserved verbatim from lines 482-599 of the previous file.
export function MemoryCard({
  memory: m,
  copiedId,
  runtimes,
  onCopy,
  onEdit,
  onDelete,
  onPromote,
  onCopyToProject,
}: MemoryCardProps) {
  return (
    <div className="group flex items-start gap-3 rounded-xl border bg-card p-4">
      <div className="flex min-w-0 flex-1 flex-col gap-2">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap items-center gap-1.5 text-xs">
            <span
              className={`rounded-md px-2 py-0.5 font-medium ${typeBadge[m.type] ?? typeBadge.Note}`}
            >
              {m.type}
            </span>
            {m.status !== "Active" && (
              <span
                className={`rounded-md px-2 py-0.5 font-medium ${statusBadge[m.status] ?? statusBadge.Archived}`}
              >
                {m.status}
              </span>
            )}
            <span
              className={
                m.scope === "global"
                  ? "rounded-md bg-blue-500/10 px-2 py-0.5 font-medium text-blue-600"
                  : "rounded-md bg-amber-500/10 px-2 py-0.5 font-medium text-amber-600"
              }
            >
              {m.scope === "global"
                ? "🌐 Global"
                : m.project
                ? `📁 ${m.project.id}`
                : "📁 Project"}
            </span>
          </div>
        </div>

        <p className="max-w-2xl whitespace-pre-wrap break-words text-sm">
          {m.content}
        </p>

        <div className="flex flex-wrap items-center gap-1.5 text-xs">
          {m.tags?.map((tag) => (
            <span
              key={tag}
              className="rounded-md bg-secondary px-2 py-0.5 text-secondary-foreground"
            >
              #{tag}
            </span>
          ))}
        </div>

        <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1 text-[11px] text-muted-foreground">
          <button
            onClick={() => onCopy(m.id, m.id)}
            className="inline-flex items-center gap-1 font-mono text-[11px] hover:text-foreground transition-colors cursor-pointer bg-muted/50 hover:bg-muted rounded px-1.5 py-0.5"
            title={`Copy full ID: ${m.id}`}
          >
            <span className="break-all">ID: {m.id}</span>
            {copiedId === m.id ? (
              <Check className="size-3 text-green-500" />
            ) : (
              <Copy className="size-3 opacity-60 hover:opacity-100" />
            )}
          </button>

          <span>•</span>
          <span>Created: {formatDate(m.createdAt)}</span>
          <span>•</span>
          <span>Updated: {formatDate(m.updatedAt || m.createdAt)}</span>
        </div>
      </div>
      <div className="flex shrink-0 gap-1">
        {m.scope === "project" && (
          <Button
            variant="ghost"
            size="sm"
            className="text-muted-foreground hover:text-foreground text-xs"
            onClick={() => onPromote(m)}
            aria-label="Promote to global"
            title="Promote to Global (copy, original stays)"
          >
            ↑ Global
          </Button>
        )}
        {m.scope === "global" && runtimes.length > 0 && (
          <select
            aria-label="Copy to project"
            title="Copy to Project"
            className="h-8 rounded-md border bg-background px-2 text-xs"
            defaultValue=""
            onChange={(e) => {
              if (e.target.value) {
                onCopyToProject(m, e.target.value)
                e.target.value = ""
              }
            }}
          >
            <option value="" disabled>
              Copy to…
            </option>
            {runtimes.map((r) => (
              <option key={r.projectRoot} value={r.projectRoot}>
                📁 {r.projectRoot.split("\\").pop() ?? r.projectRoot.split("/").pop()}
              </option>
            ))}
          </select>
        )}
        <Button
          variant="ghost"
          size="icon"
          className="text-muted-foreground hover:text-foreground"
          onClick={() => onEdit(m.id)}
          aria-label="Edit memory"
        >
          <Pencil className="size-4" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          className="text-muted-foreground hover:text-destructive"
          onClick={() => onDelete(m)}
          aria-label="Delete memory"
        >
          <Trash2 className="size-4" />
        </Button>
      </div>
    </div>
  )
}