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
//
// The search scope is a second, independent piece of URL state. It answers a
// different question: not which workspaces are read, but what the query is
// matched against. Both travel together because the page's state is URL
// state, and a shared link should reproduce the whole view.
import type { WorkspaceSelection } from "@/components/workspaces/selection"
import { selectAll } from "@/components/workspaces/selection"

const PROJECT_PARAM = "project"
const SEARCH_PARAM = "search"
const QUERY_PARAM = "q"

/**
 * What a codebase search matches the query against.
 *
 * - `content`: the file contents, through the full-text index. The original
 *   behaviour, and the default.
 * - `path`: the workspace-relative path of the file, folders included — so
 *   `src/backend` narrows to every file under that folder.
 * - `file`: the file name only — so `CodebaseList` finds the file by name
 *   and `src/backend` finds nothing at all.
 */
export type SearchScope = "content" | "path" | "file"

/** The scope a search runs in when the URL says nothing. */
export const DEFAULT_SEARCH_SCOPE: SearchScope = "content"

/**
 * The visible wording of each scope, in one place so the toggle and the hint
 * under the query box cannot drift apart.
 */
export const SEARCH_SCOPE_COPY: Record<
  SearchScope,
  { label: string; placeholder: string; hint: string }
> = {
  content: {
    label: "Content",
    placeholder: "Type to search…",
    hint: "Empty box shows recently indexed files · search runs as you type · list refreshes live when any process rebuilds the index.",
  },
  path: {
    label: "Path",
    placeholder: "Type a path to match…",
    hint: "Matches the file path relative to the workspace · “src/backend” finds every file under that folder.",
  },
  file: {
    label: "File",
    placeholder: "Type a file name to match…",
    hint: "Matches the file name only · “CodebaseList” finds the files named that and ignores the folders they sit in.",
  },
}

/** Repeated `project` params are how the endpoints bind an explicit root list. */
export function selectionFromParams(params: URLSearchParams): WorkspaceSelection {
  const roots = params.getAll(PROJECT_PARAM).map((r) => r.trim()).filter(Boolean)
  return roots.length > 0 ? { kind: "workspaces", roots } : selectAll()
}

/**
 * The search scope the URL asks for. An unknown value falls back to the
 * default rather than failing: the scope is a view preference, and a stale
 * link should still show the page.
 */
export function searchScopeFromParams(params: URLSearchParams): SearchScope {
  const value = params.get(SEARCH_PARAM)?.trim().toLowerCase()
  if (value === "path") return "path"
  if (value === "file") return "file"
  return DEFAULT_SEARCH_SCOPE
}

/** Empty string means "all"; the three endpoints treat it as scope=all. */
export function selectionQuery(sel: WorkspaceSelection): string {
  return sel.kind === "all"
    ? ""
    : sel.roots.map((r) => `${PROJECT_PARAM}=${encodeURIComponent(r)}`).join("&")
}

/**
 * The text in the query box, as the URL carries it. Trimmed, because the
 * search treats a blank box as no query at all.
 */
export function queryFromParams(params: URLSearchParams): string {
  return params.get(QUERY_PARAM)?.trim() ?? ""
}

/** The query as a URL param. An empty query adds no param at all. */
export function queryParam(query: string): string {
  const trimmed = query.trim()
  return trimmed ? `${QUERY_PARAM}=${encodeURIComponent(trimmed)}` : ""
}

/** The search scope in the URL. The default stays out of it, like the tier on the memories page. */
export function searchScopeQuery(scope: SearchScope): string {
  return scope === DEFAULT_SEARCH_SCOPE ? "" : `${SEARCH_PARAM}=${scope}`
}

function scopeSuffix(sel: WorkspaceSelection): string {
  const query = selectionQuery(sel)
  return query ? `&${query}` : "&scope=all"
}

// URL builders live here so the scope a request covers is decided in one
// place — the read (search/status) and the write (rebuild) share it by
// construction.

const SEARCH_LIMIT = 20

export function searchUrl(
  q: string,
  sel: WorkspaceSelection,
  scope: SearchScope,
): string {
  const base = `/api/codebase/search?limit=${SEARCH_LIMIT}`
  const selection = scopeSuffix(sel)
  const search = searchScopeQuery(scope)
  const scopePart = search ? `&${search}` : ""
  return q.trim()
    ? `${base}&q=${encodeURIComponent(q.trim())}${selection}${scopePart}`
    : `${base}${selection}${scopePart}`
}

export function statusUrl(sel: WorkspaceSelection): string {
  const query = selectionQuery(sel)
  return query ? `/api/codebase/status?${query}` : "/api/codebase/status?scope=all"
}

export function rebuildUrl(
  sel: WorkspaceSelection,
  full: boolean,
  cacheBuster: number,
): string {
  return `/api/codebase/rebuild-index?full=${full}&_t=${cacheBuster}${scopeSuffix(sel)}`
}
