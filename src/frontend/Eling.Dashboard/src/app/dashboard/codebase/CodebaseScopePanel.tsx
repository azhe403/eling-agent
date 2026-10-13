"use client"

import { FolderGit2, Layers } from "lucide-react"
import { cn } from "cn"
import { projectName, type ScopeSelection } from "./scope"

/** One workspace row, shaped so the panel knows nothing about runtime DTOs. */
export type ScopeRow = {
  readonly root: string
  readonly alive: boolean
}

/**
 * The codebase page's scope panel: an always-visible workspace list on wide
 * screens (the narrow-screen Select in the search form is the fallback). Click
 * a row to make it the only scope; use the checkbox to exclude or re-include a
 * workspace in the federated set. Both interactions funnel through the parent
 * so toggle rules and the URL stay in one place.
 */
export function CodebaseScopePanel({
  rows,
  selection,
  onSelectAll,
  onSelectOnly,
  onToggleRoot,
}: {
  rows: readonly ScopeRow[]
  selection: ScopeSelection
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
    selection.kind === "projects" && selection.roots.length === 1
      ? selection.roots[0]
      : null

  return (
    <nav
      aria-label="Codebase scope"
      className="sticky top-16 hidden max-h-[calc(100dvh-4rem)] w-64 shrink-0 flex-col gap-1 self-start border-r p-3 lg:flex"
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
        title="Search every workspace with a codebase index"
        className={cn(
          "flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          selection.kind === "all"
            ? "bg-accent font-medium text-accent-foreground"
            : "hover:bg-accent/60"
        )}
      >
        <Layers className="size-4 shrink-0" />
        All projects
      </button>

      <ul className="flex min-h-0 flex-col gap-0.5 overflow-y-auto">
        {rows.map(({ root, alive }) => {
          const name = projectName(root)
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
                aria-label={`Include ${name} in codebase scope`}
                title="Include this workspace in the search scope"
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
