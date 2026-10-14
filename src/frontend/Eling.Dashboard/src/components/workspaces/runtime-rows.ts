// The coordinator's runtime list, shaped into workspace rows for the shared
// workspace-list surfaces. Mirrors GET /api/coordinator/runtimes?includeRegistered=true.
//
// The DTO type is deliberately tolerant — everything past the scope root is
// optional — because payloads from older binaries omit newer fields, and a
// renamed field fails at runtime rather than at compile time. So the row
// derivation reads defensively instead of trusting the shape.

/** One runtime row from the coordinator. */
export type RuntimeEntry = {
  // The nearest .eling at or above the workspace, where memories are written —
  // not a project root. It can sit above the repository.
  headScopeRoot: string
  workspaceRoot?: string | null
  // Absent (older payloads) means enabled; an explicit false means the runtime
  // opted out of codebase indexing and never appears as a row.
  codebaseEnabled?: boolean | null
  // Present when the coordinator tracks heartbeats; absent on older payloads,
  // where a registered runtime is treated as indexed-but-unknown.
  isAlive?: boolean
}

/** One row in a workspace list. */
export type WorkspaceRow = {
  readonly root: string
  readonly alive: boolean
}

// includeRegistered: a codebase index is a read-only file in a global store
// and stays readable after its process exits, so a closed project must stay
// in the list. The default live-only mode undercounts the store by roughly the
// number of dead-but-still-indexed workspaces.
export const RUNTIMES_URL = "/api/coordinator/runtimes?includeRegistered=true"

export async function fetchRuntimes(): Promise<RuntimeEntry[]> {
  const res = await fetch(RUNTIMES_URL, { cache: "no-store" })
  if (!res.ok) throw new Error(`API returned ${res.status}`)
  const data = (await res.json()) as RuntimeEntry[]
  return Array.isArray(data) ? data : []
}

export function workspaceName(root: string | null | undefined): string {
  if (!root) return "—"
  const parts = root.split(/[/\\]+/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : root
}

/**
 * Workspace rows from the runtime list. Codebase identity is the workspace
 * (cwd), never a .eling ancestor — fall back to headScopeRoot for runtimes
 * registered by older binaries that omit workspaceRoot. Runtimes that opted
 * out of indexing never appear as rows. Dead-but-registered workspaces stay
 * listed: a codebase index is a read-only file in the global store and
 * remains searchable after its process exits. Dedup is case-insensitive
 * because the same workspace can arrive with mixed path casing from different
 * registrations.
 */
export function workspaceRowsOf(
  runtimes: readonly RuntimeEntry[],
): WorkspaceRow[] {
  const out: WorkspaceRow[] = []
  const seen = new Set<string>()
  for (const r of runtimes) {
    if (r.codebaseEnabled === false) continue
    const root = r.workspaceRoot || r.headScopeRoot
    if (!root || root === "UserScope") continue
    const key = root.toLowerCase()
    if (seen.has(key)) continue
    seen.add(key)
    out.push({ root, alive: r.isAlive === true })
  }
  return out
}
