"use client"

import { Layers } from "lucide-react"

import { cn } from "cn"
import { WorkspaceGroupToggle } from "./workspace-group-toggle"
import { WorkspaceTree } from "./workspace-tree"
import { useWorkspaceGroupMode } from "./use-workspace-group-mode"
import type { WorkspaceRow } from "./runtime-rows"
import type { WorkspaceSelection } from "./selection"

/**
 * A workspace list as a side panel: the wide-screen surface, with the
 * narrow-screen `WorkspaceSelect` as the fallback.
 *
 * The list can be arranged three ways — flat, by git repository, or by the
 * folders that hold the workspaces — and the arrangement is a view choice
 * only: the selection stays a flat list of workspace roots, and both
 * interactions funnel through the parent so the toggle rules and the URL
 * binding stay in one place.
 */
export function WorkspaceScopePanel({
  rows,
  selection,
  onSelectAll,
  onSelectOnly,
  onToggleRoots,
}: {
  rows: readonly WorkspaceRow[]
  selection: WorkspaceSelection
  onSelectAll: () => void
  onSelectOnly: (root: string) => void
  onToggleRoots: (roots: readonly string[]) => void
}) {
  const { mode, setMode } = useWorkspaceGroupMode()

  return (
    <nav
      aria-label="Workspace scope"
      className="sticky top-16 hidden max-h-[calc(100dvh-4rem)] w-72 shrink-0 flex-col gap-1 self-start border-r p-3 lg:flex"
    >
      <div className="flex items-center justify-between px-1 pb-1">
        <span className="text-xs font-medium text-muted-foreground">
          Workspaces
        </span>
        <span className="text-xs text-muted-foreground">{rows.length}</span>
      </div>

      <WorkspaceGroupToggle mode={mode} onChange={setMode} />

      <button
        type="button"
        onClick={onSelectAll}
        title="Include every workspace in the selection"
        className={cn(
          "flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          selection.kind === "all"
            ? "bg-accent font-medium text-accent-foreground"
            : "hover:bg-accent/60",
        )}
      >
        <Layers className="size-4 shrink-0" />
        All workspaces
      </button>

      <WorkspaceTree
        rows={rows}
        selection={selection}
        mode={mode}
        onSelectOnly={onSelectOnly}
        onToggleRoots={onToggleRoots}
      />
    </nav>
  )
}
