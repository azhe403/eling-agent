// The workspace list's shape, and nothing else.
//
// The arrangement used to be computed here: flat, by git repository (worktrees
// nested under their main checkout), or by the folders that hold the
// workspaces. It is computed by the coordinator now and served by
// GET /api/coordinator/workspaces?group=… — one arrangement per request — and
// the desktop client renders the same tree. What is left in this file is the
// vocabulary both sides agree on, plus the defensive mapping from the wire
// payload.
//
// The grouping is a view concern only. The selection underneath stays a flat
// list of workspace roots, so the URL and every consumer of it are untouched.

import { workspaceName, type WorkspaceGroupMode, type WorkspaceTreeNodePayload, type WorkspaceTreesPayload } from "./runtime-rows"

/** How the workspace list is arranged. */
export type { WorkspaceGroupMode }

/** One entry in the arranged list. */
export type WorkspaceNode = {
  /** Stable identity, used as the React key. Identical across arrangements. */
  readonly id: string
  /** The name shown in the list. */
  readonly label: string
  /** The path this entry stands for. */
  readonly path: string
  /** The workspace this entry selects, when it stands for one. */
  readonly root: string | null
  /** True when any workspace this entry covers has a running process. */
  readonly alive: boolean
  /** The workspaces this entry's checkbox covers. */
  readonly roots: readonly string[]
  /** True when the entry stands for a git repository rather than a plain folder. */
  readonly isRepository: boolean
  /** True when this entry's own workspace is a linked worktree. */
  readonly isWorktree: boolean
  readonly children: readonly WorkspaceNode[]
}

/**
 * The nodes the coordinator sent for one arrangement, in the shape the
 * renderer wants. A missing or empty payload becomes an empty list rather
 * than throwing, so an older binary shows an empty list instead of a blank
 * page.
 */
export function nodesFor(
  payload: WorkspaceTreesPayload,
): readonly WorkspaceNode[] {
  return nodesOf(payload.nodes)
}

function nodesOf(
  nodes: readonly WorkspaceTreeNodePayload[] | null | undefined,
): readonly WorkspaceNode[] {
  if (!Array.isArray(nodes)) return []
  return nodes.map(nodeOf)
}

function nodeOf(node: WorkspaceTreeNodePayload): WorkspaceNode {
  const path = node.path ?? node.root ?? ""
  return {
    id: node.id ?? `ws:${path}`,
    label: node.label ?? workspaceName(path),
    path,
    root: node.root ?? null,
    alive: node.alive === true,
    roots: Array.isArray(node.roots)
      ? node.roots.filter((r): r is string => typeof r === "string")
      : [],
    isRepository: node.isRepository === true,
    isWorktree: node.isWorktree === true,
    children: nodesOf(node.children),
  }
}
