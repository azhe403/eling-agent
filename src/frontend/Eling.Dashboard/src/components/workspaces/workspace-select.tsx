"use client"

import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { workspaceName, type WorkspaceRow } from "./runtime-rows"
import { selectionSummary, type WorkspaceSelection } from "./selection"

/**
 * The narrow-screen fallback for `WorkspaceScopePanel`: a Select that can
 * express one scope at a time. A multi-root selection shows as a summary —
 * no item matches the "multiple" sentinel, and the trigger label comes from
 * the selection itself, so nothing lies about state. Picking any row switches
 * to that single workspace. The parent decides when this is visible (the
 * codebase page hides it on wide screens, where the panel covers the job).
 *
 * `label` overrides the trigger text for parents whose selection is not a
 * workspace set at all (the memory page's global scope), so the trigger never
 * has to describe one.
 */
export function WorkspaceSelect({
  rows,
  selection,
  onSelectAll,
  onSelectOnly,
  label,
}: {
  rows: readonly WorkspaceRow[]
  selection: WorkspaceSelection
  onSelectAll: () => void
  onSelectOnly: (root: string) => void
  label?: string
}) {
  return (
    <Select
      value={selectValue(selection)}
      onValueChange={(v) => (!v || v === "all" ? onSelectAll() : onSelectOnly(v))}
    >
      <SelectTrigger
        aria-label="Workspace scope"
        title={selectionSummary(selection)}
        className="w-full max-w-44 shrink-0 truncate sm:max-w-72"
      >
        <SelectValue placeholder="Select workspace...">
          {() =>
            selection.kind === "all"
              ? "🌐 All workspaces"
              : (label ?? `📁 ${selectionSummary(selection)}`)}
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
        <SelectItem value="all">🌐 All workspaces</SelectItem>
        {rows.map((r) => (
          <SelectItem key={r.root} value={r.root} title={r.root}>
            <span className="flex min-w-0 flex-col items-start">
              <span>📁 {workspaceName(r.root)}</span>
              <span className="text-xs whitespace-normal break-all text-muted-foreground">
                {r.root}
              </span>
            </span>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

/** Value the Select can represent: "all", a single root, or a sentinel. */
function selectValue(selection: WorkspaceSelection): string {
  if (selection.kind === "all") return "all"
  if (selection.roots.length === 1) return selection.roots[0]
  return "multiple"
}
