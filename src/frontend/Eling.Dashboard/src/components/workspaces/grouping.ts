// The workspace list, arranged for display.
//
// A flat list of workspaces hides two facts about a machine: several of them
// are checkouts of ONE repository (a main checkout plus its linked worktrees),
// and a handful are parent folders that merely hold other workspaces. The tree
// makes both visible, and the mode decides which structure leads:
//
//   flat   - one row per workspace, the way the registry holds them
//   repo   - one entry per git repository, its worktrees nested under it
//   folder - one entry per workspace, nested by the folders that hold them,
//            with a repository's worktrees still nested under their checkout
//
// The grouping is a view concern only. The selection underneath stays a flat
// list of workspace roots, so the URL and every consumer of it are untouched.

import { workspaceName, type WorkspaceRow } from "./runtime-rows"

/** How the workspace list is arranged. */
export type WorkspaceGroupMode = "flat" | "repo" | "folder"

/** One entry in the arranged list. */
export type WorkspaceNode = {
  /** Stable identity, used as the React key. */
  readonly id: string
  /** The name shown in the list. */
  readonly label: string
  /** The path this entry stands for. */
  readonly path: string
  /** The workspace this entry selects, when it stands for one. */
  readonly row: WorkspaceRow | null
  /** The workspaces this entry's checkbox covers. */
  readonly group: readonly WorkspaceRow[]
  /** True when the entry stands for a git repository rather than a plain folder. */
  readonly isRepository: boolean
  /** True when this entry's own workspace is a linked worktree. */
  readonly isWorktree: boolean
  readonly children: readonly WorkspaceNode[]
}

/** The checkouts of one repository, gathered from the workspace rows. */
type RepositoryGroup = {
  /** The comparison key of the group's path. */
  readonly key: string
  /** The path the group stands for: the repository's main checkout. */
  readonly path: string
  /** The main checkout, when it is itself a registered workspace. */
  readonly primary: WorkspaceRow | null
  /** Every registered workspace belonging to this repository. */
  readonly members: readonly WorkspaceRow[]
}

/**
 * Path comparison key. Separators and letter case are not significant on
 * Windows, and the registry hands back the same folder spelled differently by
 * different runtimes, so every comparison goes through here.
 */
function pathKey(path: string): string {
  return path.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase()
}

/** True when `ancestor` is a strict path ancestor of `descendant`. */
function isAncestor(ancestor: string, descendant: string): boolean {
  const a = pathKey(ancestor)
  const d = pathKey(descendant)
  if (a === d) return false
  return d.startsWith(`${a}/`)
}

/** The repository a workspace belongs to: its own root when it is not in one. */
function repositoryKey(row: WorkspaceRow): string {
  return pathKey(row.repositoryRoot ?? row.root)
}

/**
 * The checkouts of each repository, in the order the rows arrived.
 *
 * A workspace's `repositoryRoot` is the MAIN checkout's path, so every linked
 * worktree of one repository lands in the same bucket. A workspace outside any
 * repository is a bucket of its own.
 */
function repositoryGroups(rows: readonly WorkspaceRow[]): RepositoryGroup[] {
  const buckets = new Map<string, WorkspaceRow[]>()
  for (const row of rows) {
    const key = repositoryKey(row)
    const bucket = buckets.get(key)
    if (bucket) {
      bucket.push(row)
    } else {
      buckets.set(key, [row])
    }
  }

  return [...buckets.values()].map((members) => {
    const primary =
      members.find((m) => pathKey(m.root) === repositoryKey(m)) ?? null
    const path = primary?.root ?? members[0].repositoryRoot ?? members[0].root
    return { key: pathKey(path), path, primary, members }
  })
}

/** A plain workspace entry: a leaf that selects one workspace. */
function workspaceNode(row: WorkspaceRow, isWorktree: boolean): WorkspaceNode {
  return {
    id: `ws:${pathKey(row.root)}`,
    label: workspaceName(row.root),
    path: row.root,
    row,
    group: [row],
    isRepository: false,
    isWorktree,
    children: [],
  }
}

/**
 * A repository entry: its main checkout stands for the whole repository, and
 * the other checkouts of that repository hang under it.
 */
function repositoryNode(group: RepositoryGroup): WorkspaceNode {
  const others = group.members.filter((m) => m !== group.primary)
  return {
    id: `repo:${group.key}`,
    label: workspaceName(group.path),
    path: group.path,
    row: group.primary,
    group: group.members,
    isRepository: true,
    isWorktree: false,
    children: others
      .map((row) => workspaceNode(row, true))
      .sort((a, b) => a.label.localeCompare(b.label)),
  }
}

function byLabel(a: WorkspaceNode, b: WorkspaceNode): number {
  return a.label.localeCompare(b.label)
}

/** One row per workspace, the way the registry holds them. */
function flatNodes(rows: readonly WorkspaceRow[]): WorkspaceNode[] {
  return rows.map((row) => workspaceNode(row, false)).sort(byLabel)
}

/** One entry per repository, its worktrees nested under it. */
function repositoryNodes(rows: readonly WorkspaceRow[]): WorkspaceNode[] {
  return repositoryGroups(rows).map(repositoryNode).sort(byLabel)
}

/**
 * One entry per repository, nested by the folders that hold them.
 *
 * The nesting comes from the workspace paths themselves: an entry sits under
 * the nearest other entry that contains it. A worktree never places itself —
 * it hangs under the repository it belongs to, which is the one relationship
 * a plain folder tree would otherwise lose.
 */
function folderNodes(rows: readonly WorkspaceRow[]): WorkspaceNode[] {
  const groups = repositoryGroups(rows)
  const byKey = new Map(groups.map((g) => [g.key, g]))

  const nested = new Map<string, string[]>()
  for (const group of groups) {
    const parent = nearestContainer(groups, group)
    if (!parent) continue
    const children = nested.get(parent.key) ?? []
    children.push(group.key)
    nested.set(parent.key, children)
  }

  const build = (group: RepositoryGroup): WorkspaceNode => {
    const base = repositoryNode(group)
    const children = (nested.get(group.key) ?? [])
      .map((key) => byKey.get(key))
      .filter((g): g is RepositoryGroup => g !== undefined)
      .map(build)
    return { ...base, children: [...base.children, ...children].sort(byLabel) }
  }

  return groups
    .filter((group) => !nearestContainer(groups, group))
    .map(build)
    .sort(byLabel)
}

/** The nearest group whose path contains the given group's path. */
function nearestContainer(
  groups: readonly RepositoryGroup[],
  group: RepositoryGroup,
): RepositoryGroup | null {
  let best: RepositoryGroup | null = null
  for (const candidate of groups) {
    if (candidate === group) continue
    if (!isAncestor(candidate.path, group.path)) continue
    // A deeper container is a better parent than a shallower one.
    if (!best || isAncestor(best.path, candidate.path)) best = candidate
  }
  return best
}

/** The workspace list, arranged for the given mode. */
export function buildWorkspaceTree(
  rows: readonly WorkspaceRow[],
  mode: WorkspaceGroupMode,
): readonly WorkspaceNode[] {
  switch (mode) {
    case "repo":
      return repositoryNodes(rows)
    case "folder":
      return folderNodes(rows)
    default:
      return flatNodes(rows)
  }
}

/**
 * Every workspace a node stands for, deepest first. A repository node covers
 * all of its checkouts; a plain node covers only itself.
 */
export function nodeWorkspaces(node: WorkspaceNode): readonly WorkspaceRow[] {
  return node.group
}
