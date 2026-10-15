"use client"

import { FolderGit2, Layers } from "lucide-react"
import { cn } from "cn"
import { workspaceName, type WorkspaceRow } from "./runtime-rows"
import type { WorkspaceSelection } from "./selection"

/**
 * A workspace list as a side panel: the wide-screen surface, with the
 * narrow-screen `WorkspaceSelect` as the fallback. Click a row to make it
 * the only selection; use the checkbox to exclude or re-include a workspace
 * in the set. Both interactions funnel through the parent so toggle rules
 * and any URL binding stay in one place.
 */
export function WorkspaceScopePanel({
  rows,
  selection,
  onSelectAll,
  onSelectOnly,
  onToggleRoot,
}: {
  rows: readonly WorkspaceRow[]
  selection: WorkspaceSelection
  onSelectAll: () => void
  onSelectOnly: (root: string) => void
  onToggleRoot: (root: string) => void
}) {
  // In "all" every row is implicitly included, so the boxes read as checked.
  const included =
    selection.kind === "all"
      ? new Set(rows.map((r) => r.root))
      : new Set(selection.roots)
  const singleRoot =
    selection.kind === "workspaces" && selection.roots.length === 1
      ? selection.roots[0]
      : null

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

      <button
        type="button"
        onClick={onSelectAll}
        title="Include every workspace in the selection"
        className={cn(
          "flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          selection.kind === "all"
            ? "bg-accent font-medium text-accent-foreground"
            : "hover:bg-accent/60"
        )}
      >
        <Layers className="size-4 shrink-0" />
        All workspaces
      </button>

      <ul className="flex min-h-0 flex-col gap-0.5 overflow-y-auto pr-2 [scrollbar-gutter:stable]">
        {rows.map(({ root, alive }) => {
          const name = workspaceName(root)
          return (
            <li
              key={root}
              className="flex items-center gap-2 rounded-md px-1 py-1 hover:bg-accent/60"
            >
              <input
                type="checkbox"
                className="accent-primary"
                checked={included.has(root)}
                onChange={() => onToggleRoot(root)}
                aria-label={`Include ${name}`}
                title="Include this workspace in the selection"
              />
              <button
                type="button"
                onClick={() => onSelectOnly(root)}
                title={root}
                className={cn(
                  "flex min-w-0 flex-1 cursor-pointer items-center gap-1.5 rounded-md text-left text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  singleRoot === root ? "font-medium" : undefined
                )}
              >
                <FolderGit2 className="size-4 shrink-0 text-muted-foreground" />
                <span className="truncate">{name}</span>
              </button>
              <span
                className={cn(
                  "size-2 shrink-0 rounded-full",
                  alive ? "bg-emerald-500" : "bg-muted-foreground/40"
                )}
                title={alive ? "Runtime is live" : "Offline — or liveness unknown"}
              />
            </li>
          )
        })}
        {rows.length === 0 && (
          <li className="p-2 text-sm text-muted-foreground">
            No indexed workspaces.
          </li>
        )}
      </ul>
    </nav>
  )
}
