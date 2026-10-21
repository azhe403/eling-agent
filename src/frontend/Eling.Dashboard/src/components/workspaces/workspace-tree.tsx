"use client"

import { useEffect, useMemo, useRef, useState } from "react"
import { ChevronRight, FolderGit2, GitBranch } from "lucide-react"

import { cn } from "cn"
import { buildWorkspaceTree, type WorkspaceGroupMode, type WorkspaceNode } from "./grouping"
import type { WorkspaceRow } from "./runtime-rows"
import type { WorkspaceSelection } from "./selection"

/**
 * The workspace list as a tree.
 *
 * Three arrangements share one renderer: flat, one row per workspace; by
 * repository, one entry per git repository with its worktrees nested under it;
 * and by folder, one entry per workspace nested by the folders that hold it.
 * The selection underneath stays a flat list of workspace roots, so grouping
 * changes what the list looks like and nothing else.
 */
export function WorkspaceTree({
  rows,
  selection,
  mode,
  onSelectOnly,
  onToggleRoots,
}: {
  rows: readonly WorkspaceRow[]
  selection: WorkspaceSelection
  mode: WorkspaceGroupMode
  onSelectOnly: (root: string) => void
  onToggleRoots: (roots: readonly string[]) => void
}) {
  const nodes = useMemo(() => buildWorkspaceTree(rows, mode), [rows, mode])
  // Expansion is a user override on top of a default: a repository with
  // several checkouts starts open, because that grouping is the point of the
  // mode; everything else starts closed until asked for.
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set())
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set())
  const included = useMemo(
    () =>
      selection.kind === "all"
        ? new Set(rows.map((r) => r.root))
        : new Set(selection.roots),
    [rows, selection],
  )

  const toggleOpen = (id: string, open: boolean) => {
    if (open) {
      setExpanded((prev) => new Set(prev).add(id))
      setCollapsed((prev) => {
        const next = new Set(prev)
        next.delete(id)
        return next
      })
      return
    }
    setCollapsed((prev) => new Set(prev).add(id))
    setExpanded((prev) => {
      const next = new Set(prev)
      next.delete(id)
      return next
    })
  }

  if (rows.length === 0) {
    return (
      <p className="p-2 text-sm text-muted-foreground">No indexed workspaces.</p>
    )
  }

  return (
    <ul className="flex min-h-0 flex-col gap-0.5 overflow-y-auto pr-2 [scrollbar-gutter:stable]">
      {nodes.map((node) => (
        <TreeNode
          key={node.id}
          node={node}
          depth={0}
          included={included}
          expanded={expanded}
          collapsed={collapsed}
          onToggleOpen={toggleOpen}
          onSelectOnly={onSelectOnly}
          onToggleRoots={onToggleRoots}
        />
      ))}
    </ul>
  )
}

/** One entry of the tree, with its children. */
function TreeNode({
  node,
  depth,
  included,
  expanded,
  collapsed,
  onToggleOpen,
  onSelectOnly,
  onToggleRoots,
}: {
  node: WorkspaceNode
  depth: number
  included: ReadonlySet<string>
  expanded: ReadonlySet<string>
  collapsed: ReadonlySet<string>
  onToggleOpen: (id: string, open: boolean) => void
  onSelectOnly: (root: string) => void
  onToggleRoots: (roots: readonly string[]) => void
}) {
  const group = node.group
  const includedCount = group.filter((r) => included.has(r.root)).length
  const allIncluded = group.length > 0 && includedCount === group.length
  const someIncluded = includedCount > 0 && !allIncluded
  const hasChildren = node.children.length > 0
  const open = expanded.has(node.id) || (!collapsed.has(node.id) && group.length > 1)
  const roots = group.map((r) => r.root)
  const alive = group.some((r) => r.alive)
  const row = node.row

  return (
    <li className="flex flex-col">
      <div
        className="flex items-center gap-1.5 rounded-md px-1 py-1 hover:bg-accent/60"
        style={{ paddingLeft: `${depth * 12 + 4}px` }}
      >
        {hasChildren ? (
          <button
            type="button"
            onClick={() => onToggleOpen(node.id, !open)}
            aria-label={open ? `Collapse ${node.label}` : `Expand ${node.label}`}
            className="flex size-4 shrink-0 cursor-pointer items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <ChevronRight
              className={cn("size-3.5 transition-transform", open && "rotate-90")}
            />
          </button>
        ) : (
          <span className="size-4 shrink-0" />
        )}

        <GroupCheckbox
          checked={allIncluded}
          indeterminate={someIncluded}
          onChange={() => onToggleRoots(roots)}
          label={`Include ${node.label}`}
          title={
            group.length > 1
              ? `Include or exclude every checkout of ${node.label}`
              : "Include this workspace in the selection"
          }
        />

        {row ? (
          <button
            type="button"
            onClick={() => onSelectOnly(row.root)}
            title={node.path}
            className="flex min-w-0 flex-1 cursor-pointer items-center gap-1.5 rounded-md text-left text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {node.isWorktree ? (
              <GitBranch className="size-4 shrink-0 text-muted-foreground" />
            ) : (
              <FolderGit2 className="size-4 shrink-0 text-muted-foreground" />
            )}
            <span
              className={cn(
                "truncate",
                node.isWorktree && "text-muted-foreground",
              )}
            >
              {node.label}
            </span>
            {group.length > 1 && (
              <span className="shrink-0 rounded bg-muted px-1 text-[10px] text-muted-foreground">
                {group.length}
              </span>
            )}
          </button>
        ) : (
          <span
            title={node.path}
            className="flex min-w-0 flex-1 items-center gap-1.5 text-sm"
          >
            <FolderGit2 className="size-4 shrink-0 text-muted-foreground" />
            <span className="truncate">{node.label}</span>
            {group.length > 1 && (
              <span className="shrink-0 rounded bg-muted px-1 text-[10px] text-muted-foreground">
                {group.length}
              </span>
            )}
          </span>
        )}

        <span
          className={cn(
            "size-2 shrink-0 rounded-full",
            alive ? "bg-emerald-500" : "bg-muted-foreground/40",
          )}
          title={alive ? "Runtime is live" : "Offline — or liveness unknown"}
        />
      </div>

      {hasChildren && open && (
        <ul className="flex flex-col gap-0.5">
          {node.children.map((child) => (
            <TreeNode
              key={child.id}
              node={child}
              depth={depth + 1}
              included={included}
              expanded={expanded}
              collapsed={collapsed}
              onToggleOpen={onToggleOpen}
              onSelectOnly={onSelectOnly}
              onToggleRoots={onToggleRoots}
            />
          ))}
        </ul>
      )}
    </li>
  )
}

/**
 * A checkbox that can also read as "partly checked", for a group whose
 * members are only partly in the selection.
 */
function GroupCheckbox({
  checked,
  indeterminate,
  onChange,
  label,
  title,
}: {
  checked: boolean
  indeterminate: boolean
  onChange: () => void
  label: string
  title: string
}) {
  const ref = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (ref.current) ref.current.indeterminate = indeterminate
  }, [indeterminate])

  return (
    <input
      ref={ref}
      type="checkbox"
      className="accent-primary"
      checked={checked}
      onChange={onChange}
      aria-label={label}
      title={title}
    />
  )
}
