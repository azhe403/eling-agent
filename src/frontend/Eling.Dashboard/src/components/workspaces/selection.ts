// The workspace-selection model, shared by every surface that narrows a list
// to a set of workspaces. URL binding and endpoint builders belong to the
// consumer (the codebase page keeps those in ./codebase/scope.ts), because
// where a selection lives in an address bar is a page decision, not a
// property of the selection itself.

import { workspaceName } from "./runtime-rows"

/** Every workspace, or an explicit subset. */
export type WorkspaceSelection =
  | { readonly kind: "all" }
  | { readonly kind: "workspaces"; readonly roots: readonly string[] }

/** The default: every workspace. */
export function selectAll(): WorkspaceSelection {
  return { kind: "all" }
}

/** Picking one row: that workspace becomes the whole selection. */
export function selectOnly(root: string): WorkspaceSelection {
  return { kind: "workspaces", roots: [root] }
}

/**
 * Checkbox semantics: membership in the set. In "all" every row is implicitly
 * included, so unchecking one yields the explicit set minus it; re-checking
 * the final excluded row collapses back to "all" — a complete set and "all"
 * are the same federation, and the UI should say that instead of restating
 * every root.
 */
export function toggleRoot(
  current: WorkspaceSelection,
  root: string,
  allRoots: readonly string[],
): WorkspaceSelection {
  const included =
    current.kind === "all" ? new Set(allRoots) : new Set(current.roots)
  if (included.has(root)) included.delete(root)
  else included.add(root)
  return fromIncluded(included, allRoots)
}

function fromIncluded(
  included: ReadonlySet<string>,
  allRoots: readonly string[],
): WorkspaceSelection {
  if (included.size === 0) return selectAll()
  if (allRoots.every((r) => included.has(r))) return selectAll()
  // Order follows the row list, so the same set always produces the same
  // selection no matter which order rows were clicked; roots absent from the
  // list (a workspace removed since the selection was written) keep their
  // place at the end.
  return {
    kind: "workspaces",
    roots: [
      ...allRoots.filter((r) => included.has(r)),
      ...[...included].filter((r) => !allRoots.includes(r)),
    ],
  }
}

/** Human label for pickers and summaries: all, one name, or a count. */
export function selectionSummary(selection: WorkspaceSelection): string {
  if (selection.kind === "all") return "All workspaces"
  if (selection.roots.length === 1) return workspaceName(selection.roots[0])
  return `${selection.roots.length} workspaces`
}
