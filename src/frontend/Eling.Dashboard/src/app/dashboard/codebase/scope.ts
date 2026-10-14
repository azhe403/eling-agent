// The codebase page's binding of the shared WorkspaceSelection
// (@/components/workspaces/selection) to this page's URL, plus the endpoint
// URLs a selection drives.
//
// One selection value feeds three consumers on this page — search, status
// tiles, and rebuild — because the tiles and the index targets must describe
// the same roots the result list reads (locked on the backend by
// CodebaseRebuildScopeTests). The URL query is the source of truth for that
// value: no project params means "all", otherwise the explicit root list, so
// reload, back/forward and shared links all land on one scope.
import type { WorkspaceSelection } from "@/components/workspaces/selection"
import { selectAll } from "@/components/workspaces/selection"

const PROJECT_PARAM = "project"

/** Repeated `project` params are how the endpoints bind an explicit root list. */
export function selectionFromParams(params: URLSearchParams): WorkspaceSelection {
  const roots = params.getAll(PROJECT_PARAM).map((r) => r.trim()).filter(Boolean)
  return roots.length > 0 ? { kind: "workspaces", roots } : selectAll()
}

/** Empty string means "all"; the three endpoints treat it as scope=all. */
export function selectionQuery(sel: WorkspaceSelection): string {
  return sel.kind === "all"
    ? ""
    : sel.roots.map((r) => `${PROJECT_PARAM}=${encodeURIComponent(r)}`).join("&")
}

function scopeSuffix(sel: WorkspaceSelection): string {
  const query = selectionQuery(sel)
  return query ? `&${query}` : "&scope=all"
}

// URL builders live here so the scope a request covers is decided in one
// place — the read (search/status) and the write (rebuild) share it by
// construction.

const SEARCH_LIMIT = 20

export function searchUrl(q: string, sel: WorkspaceSelection): string {
  const base = `/api/codebase/search?limit=${SEARCH_LIMIT}`
  const scope = scopeSuffix(sel)
  return q.trim()
    ? `${base}&q=${encodeURIComponent(q.trim())}${scope}`
    : `${base}${scope}`
}

export function statusUrl(sel: WorkspaceSelection): string {
  const query = selectionQuery(sel)
  return query ? `/api/codebase/status?${query}` : `/api/codebase/status?scope=all`
}

export function rebuildUrl(
  sel: WorkspaceSelection,
  full: boolean,
  cacheBuster: number,
): string {
  return `/api/codebase/rebuild-index?full=${full}&_t=${cacheBuster}${scopeSuffix(sel)}`
}
