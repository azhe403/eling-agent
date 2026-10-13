// One selection value feeds three consumers on the codebase page — search,
// status tiles, and rebuild — because the tiles and the index targets must
// describe the same roots the result list reads (locked on the backend by
// CodebaseRebuildScopeTests). The URL query is the source of truth for that
// value, so reload, back/forward and shared links all land on one scope.

/** Every registered workspace, federated server-side on scope=all. */
export type ScopeSelection =
  | { readonly kind: "all" }
  | { readonly kind: "projects"; readonly roots: readonly string[] }

/** The page default: every workspace with a codebase index. */
export function selectAll(): ScopeSelection {
  return { kind: "all" }
}

/** Clicking one row: that workspace becomes the whole selection. */
export function selectOnly(root: string): ScopeSelection {
  return { kind: "projects", roots: [root] }
}

/**
 * Checkbox semantics: membership in the federated set. In "all" every row is
 * implicitly included, so unchecking one yields the explicit set minus it;
 * re-checking the final excluded row collapses back to "all" — a complete set
 * and scope=all are the same federation, and the URL should say that instead
 * of restating every root.
 */
export function toggleRoot(
  current: ScopeSelection,
  root: string,
  allRoots: readonly string[],
): ScopeSelection {
  const included =
    current.kind === "all" ? new Set(allRoots) : new Set(current.roots)
  if (included.has(root)) included.delete(root)
  else included.add(root)
  return fromIncluded(included, allRoots)
}

function fromIncluded(
  included: ReadonlySet<string>,
  allRoots: readonly string[],
): ScopeSelection {
  if (included.size === 0) return selectAll()
  if (allRoots.every((r) => included.has(r))) return selectAll()
  // Order follows the row list, so the same set always produces the same URL
  // no matter which order rows were clicked; roots absent from the list (a
  // workspace removed since the URL was written) keep their place at the end.
  return {
    kind: "projects",
    roots: [
      ...allRoots.filter((r) => included.has(r)),
      ...[...included].filter((r) => !allRoots.includes(r)),
    ],
  }
}

const PROJECT_PARAM = "project"

/** Repeated `project` params are how the endpoints bind an explicit root list. */
export function selectionFromParams(params: URLSearchParams): ScopeSelection {
  const roots = params.getAll(PROJECT_PARAM).map((r) => r.trim()).filter(Boolean)
  return roots.length > 0 ? { kind: "projects", roots } : selectAll()
}

/** Empty string means "all"; the three endpoints treat it as scope=all. */
export function selectionQuery(sel: ScopeSelection): string {
  return sel.kind === "all"
    ? ""
    : sel.roots.map((r) => `${PROJECT_PARAM}=${encodeURIComponent(r)}`).join("&")
}

function scopeSuffix(sel: ScopeSelection): string {
  const query = selectionQuery(sel)
  return query ? `&${query}` : "&scope=all"
}

/** Human label for the narrow-screen Select and the panel summary. */
export function scopeSummary(sel: ScopeSelection): string {
  if (sel.kind === "all") return "All projects"
  if (sel.roots.length === 1) return projectName(sel.roots[0])
  return `${sel.roots.length} workspaces`
}

// URL builders, moved out of the component so the scope a request covers is
// decided in one place — the read (search/status) and the write (rebuild)
// share it by construction.

const SEARCH_LIMIT = 20

export function searchUrl(q: string, sel: ScopeSelection): string {
  const base = `/api/codebase/search?limit=${SEARCH_LIMIT}`
  const scope = scopeSuffix(sel)
  return q.trim()
    ? `${base}&q=${encodeURIComponent(q.trim())}${scope}`
    : `${base}${scope}`
}

export function statusUrl(sel: ScopeSelection): string {
  const query = selectionQuery(sel)
  return query ? `/api/codebase/status?${query}` : `/api/codebase/status?scope=all`
}

export function rebuildUrl(
  sel: ScopeSelection,
  full: boolean,
  cacheBuster: number,
): string {
  return `/api/codebase/rebuild-index?full=${full}&_t=${cacheBuster}${scopeSuffix(sel)}`
}

export function projectName(root: string | null | undefined): string {
  if (!root) return "—"
  const parts = root.split(/[/\\]+/).filter(Boolean)
  return parts.length > 0 ? parts[parts.length - 1] : root
}
