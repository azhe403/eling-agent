// The memory page's binding of the workspace selection to this page's URL, plus
// the endpoint URLs a selection drives. Mirrors ../codebase/scope.ts: the
// selection model itself is shared (@/components/workspaces/selection), and
// where a selection lives in the address bar is a page decision. The URL query
// is the source of truth — no project params means "all", `global=1` the global
// scope, otherwise the explicit workspace root list — so reload, back/forward
// and shared links all land on the same scope the list describes.

import {
  selectAll,
  type WorkspaceSelection,
} from "@/components/workspaces/selection"

const PROJECT_PARAM = "project"
const GLOBAL_PARAM = "global"
const LEGACY_SCOPE_PARAM = "scope"
const TIER_PARAM = "tier"

/**
 * What the memory list covers: every project (the aggregated view), or an
 * explicit workspace selection that may carry the global scope on top —
 * `global` is additive, so a workspace list plus global is one view, not a
 * toggle that forgets the workspaces.
 */
export type MemoryScope =
  | { readonly kind: "all" }
  | {
      readonly kind: "selection"
      readonly roots: readonly string[]
      readonly global: boolean
    }

/** shared (committed) | local-only (machine-only) | both. Single project only. */
export type MemoryTier = "project" | "project-local" | "all"

export function selectAllProjects(): MemoryScope {
  return { kind: "all" }
}

/** The global scope on its own: a selection with no workspace roots. */
export function selectGlobalScope(): MemoryScope {
  return { kind: "selection", roots: [], global: true }
}

/** A workspace selection; `global` adds the global scope on top of it. */
export function selectWorkspaces(
  roots: readonly string[],
  global = false,
): MemoryScope {
  return { kind: "selection", roots, global }
}

/** The selected workspace roots, empty for the aggregated view. */
export function rootsOf(sel: MemoryScope): readonly string[] {
  return sel.kind === "selection" ? sel.roots : []
}

/** Whether the global scope rides along additively. */
export function globalOn(sel: MemoryScope): boolean {
  return sel.kind === "selection" && sel.global
}

/**
 * What the shared selector components render. The global-only view checks
 * nothing on purpose: it is one scope that lives outside every workspace, not
 * "every workspace", so the panel must not claim otherwise.
 */
export function toWorkspaceSelection(sel: MemoryScope): WorkspaceSelection {
  return sel.kind === "all" ? selectAll() : { kind: "workspaces", roots: sel.roots }
}

/**
 * A panel/select interaction expressed as a scope. `global` carries the
 * caller's current global flag, so picking or toggling workspaces while the
 * global scope rides along keeps it instead of silently dropping it.
 */
export function fromWorkspaceSelection(
  sel: WorkspaceSelection,
  global: boolean,
): MemoryScope {
  return sel.kind === "all"
    ? selectAllProjects()
    : { kind: "selection", roots: sel.roots, global }
}

/** True when the tier toggle applies: exactly one project is selected. */
export function tierApplies(sel: MemoryScope): boolean {
  return sel.kind === "selection" && sel.roots.length === 1
}

export function tierFromParams(
  params: URLSearchParams,
  applies: boolean,
): MemoryTier {
  if (!applies) return "all"
  const v = params.get(TIER_PARAM)
  return v === "project-local" || v === "project" ? v : "all"
}

export function selectionFromParams(params: URLSearchParams): MemoryScope {
  const global = params.get(GLOBAL_PARAM) === "1"
  const roots = params
    .getAll(PROJECT_PARAM)
    .map((r) => r.trim())
    .filter(Boolean)
  if (roots.length > 0) return { kind: "selection", roots, global }
  if (global) return selectGlobalScope()
  // Legacy links (?scope=<root> / ?scope=global) keep working on read.
  const legacy = params.get(LEGACY_SCOPE_PARAM)?.trim()
  if (legacy === "global") return selectGlobalScope()
  if (legacy && legacy !== "all") {
    return { kind: "selection", roots: [legacy], global: false }
  }
  return selectAllProjects()
}

/** Empty string means "all"; global is one param; workspaces repeat `project`. */
export function selectionQuery(sel: MemoryScope): string {
  if (sel.kind === "all") return ""
  const parts = sel.roots.map(
    (r) => `${PROJECT_PARAM}=${encodeURIComponent(r)}`,
  )
  if (sel.global) parts.push(`${GLOBAL_PARAM}=1`)
  return parts.join("&")
}

/**
 * "all" is the default tier, so it stays out of the URL; only the two
 * narrower tiers need a param. A bare workspace URL therefore reads as
 * shared+local, which is what the toggle shows as active.
 */
export function tierQuery(sel: MemoryScope, tier: MemoryTier): string {
  if (!tierApplies(sel) || tier === "all") return ""
  return `${TIER_PARAM}=${tier}`
}

/** One memory-list read per URL. */
export type MemoryRequest = {
  readonly url: string
}

const LAST_SELECTION_KEY = "memories:lastSelection"

/**
 * The query string of the last selection this browser made, so a bare visit to
 * the page lands where the user left off. The URL remains the source of truth
 * the moment it carries any selection param — this only fills in a bare
 * address, and shared links or back/forward always win.
 */
export function saveLastSelection(query: string): void {
  try {
    if (query) localStorage.setItem(LAST_SELECTION_KEY, query)
    else localStorage.removeItem(LAST_SELECTION_KEY)
  } catch {
    // Storage unavailable (private mode, disabled): the position simply is
    // not remembered, which costs nothing but the convenience.
  }
}

/** The saved query string, or null when there is nothing (or nowhere) to read. */
export function loadLastSelection(): string | null {
  try {
    return localStorage.getItem(LAST_SELECTION_KEY)
  } catch {
    return null
  }
}

/**
 * The page's text and type filters. They travel to the backend as query
 * params: the data already lives there, so counting and filtering the merged
 * view happen where the merge happens — the browser only renders the answer.
 */
export type MemoryFilter = {
  readonly type: string
  readonly q: string
}

/** "All" and empty text mean no filter at all, so they add no params. */
function filterParams(filter: MemoryFilter): string[] {
  const parts: string[] = []
  if (filter.type && filter.type !== "All") {
    parts.push(`type=${encodeURIComponent(filter.type)}`)
  }
  if (filter.q) {
    parts.push(`q=${encodeURIComponent(filter.q)}`)
  }
  return parts
}

/**
 * The URLs the memory list reads for this scope — one call is the whole view.
 * A workspace selection is a single request carrying every selected root plus
 * `includeAncestors` (and `global` when the global scope rides along); the
 * backend merges the chains, both tiers and the global scope, applies the
 * type/query filters and counts the result, so nothing is stitched or counted
 * here. No `limit` is sent on purpose: the page shows every memory the
 * selection matches, and the metadata's counts stay available for the day
 * paging comes back.
 */
export function memoryRequests(
  sel: MemoryScope,
  tier: MemoryTier,
  cacheBuster: number,
  filter: MemoryFilter,
): readonly MemoryRequest[] {
  if (sel.kind === "all") {
    const params = filterParams(filter).join("&")
    return [
      {
        url: `/api/aggregated/memories${params ? `?${params}&` : "?"}_t=${cacheBuster}`,
      },
    ]
  }
  if (sel.roots.length === 0) {
    const params = filterParams(filter).join("&")
    return [
      {
        url: `/api/global/memories${params ? `?${params}&` : "?"}_t=${cacheBuster}`,
      },
    ]
  }
  const projects = sel.roots
    .map((r) => `project=${encodeURIComponent(r)}`)
    .join("&")
  const params = [
    projects,
    "includeAncestors=true",
    tier === "project" ? "" : `tier=${tier}`,
    sel.global ? "global=true" : "",
    ...filterParams(filter),
  ]
    .filter(Boolean)
    .join("&")
  return [
    {
      url: `/api/project/memories?${params}&_t=${cacheBuster}`,
    },
  ]
}
