// The coordinator's workspace list, shaped for the shared workspace-list
// surfaces. Mirrors GET /api/coordinator/workspaces.
//
// The coordinator owns the arrangement: it answers with the deduplicated rows
// AND the same list pre-arranged three ways (flat, by repository, by folder).
// Nothing here groups anything — the client picks one arrangement and renders
// it, so the dashboard and the desktop cannot drift apart again.
//
// The payload type is deliberately tolerant — every field past the root is
// optional — because payloads from older binaries omit newer fields, and a
// renamed field fails at runtime rather than at compile time. So the mapping
// in grouping.ts reads defensively instead of trusting the shape.

/** One workspace row as the coordinator sends it. */
export type WorkspaceRowPayload = {
  root?: string | null
  // The folder's last segment, already computed by the coordinator.
  name?: string | null
  // The main worktree root of the git repository the workspace belongs to, or
  // null when it is not inside one.
  repositoryRoot?: string | null
  alive?: boolean | null
  // How many live processes collapsed into this row; 0 when the workspace is
  // remembered but nothing is running.
  processCount?: number | null
}

/** One node of one arrangement, as the coordinator sends it. */
export type WorkspaceTreeNodePayload = {
  // Stable across arrangements: `ws:{key}` for a workspace, `repo:{key}` for a
  // repository.
  id?: string | null
  label?: string | null
  path?: string | null
  // The workspace this node stands for, or null for a repository whose main
  // checkout is not registered.
  root?: string | null
  alive?: boolean | null
  // Every workspace root this node's checkbox covers.
  roots?: readonly string[] | null
  isRepository?: boolean | null
  isWorktree?: boolean | null
  children?: readonly WorkspaceTreeNodePayload[] | null
}

/** The whole answer: the rows plus all three arrangements. */
export type WorkspaceTreesPayload = {
  rows?: readonly WorkspaceRowPayload[] | null
  flat?: readonly WorkspaceTreeNodePayload[] | null
  repo?: readonly WorkspaceTreeNodePayload[] | null
  folder?: readonly WorkspaceTreeNodePayload[] | null
}

/** One row in a workspace list. */
export type WorkspaceRow = {
  readonly root: string
  readonly alive: boolean
  readonly repositoryRoot?: string | null
}

// Registered workspaces stay listed even after their process exits: a codebase
// index is a read-only file in a global store and remains readable, so the
// live-only list would undercount the store by roughly the number of
// dead-but-still-indexed workspaces. The coordinator decides that; the client
// just asks for the list.
export const WORKSPACE_TREES_URL = "/api/coordinator/workspaces"

export async function fetchWorkspaceTrees(): Promise<WorkspaceTreesPayload> {
  const res = await fetch(WORKSPACE_TREES_URL, { cache: "no-store" })
  if (!res.ok) throw new Error(`API returned ${res.status}`)
  const data = (await res.json()) as WorkspaceTreesPayload | null
  return data ?? {}
}

export function workspaceName(root: string | null | undefined): string {
  if (!root) return "—"
  const parts = root.split(/[/\\]+/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : root
}
