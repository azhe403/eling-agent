"use client"

import type { WorkspaceSelection } from "@/components/workspaces/selection"
import { selectAll } from "@/components/workspaces/selection"

export type DocKindFilter = "all" | "spec" | "plan" | "doc" | "scratch"
export type DocSortKey = "name" | "path" | "kind" | "status" | "order" | "updated"
export type DocSortOrder = "asc" | "desc"

const KINDS: DocKindFilter[] = ["all", "spec", "plan", "doc", "scratch"]
export const SORT_KEYS: DocSortKey[] = ["name", "path", "kind", "status", "order", "updated"]
export const DEFAULT_SORT: DocSortKey = "name"
export const DEFAULT_ORDER: DocSortOrder = "asc"
const WORKSPACE_PARAM = "workspace"

export function docKinds(): DocKindFilter[] { return KINDS }

export function docKindLabel(k: DocKindFilter): string {
  if (k === "all") return "All"
  return k.charAt(0).toUpperCase() + k.slice(1)
}

/** Repeated `workspace` params are an explicit root list; none means all. */
export function selectionFromParams(params: URLSearchParams): WorkspaceSelection {
  const roots = params.getAll(WORKSPACE_PARAM).map((root) => root.trim()).filter(Boolean)
  return roots.length > 0 ? { kind: "workspaces", roots } : selectAll()
}

export function applySelection(params: URLSearchParams, selection: WorkspaceSelection): URLSearchParams {
  const next = new URLSearchParams(params)
  next.delete(WORKSPACE_PARAM)
  if (selection.kind === "workspaces") {
    for (const root of selection.roots) next.append(WORKSPACE_PARAM, root)
  }
  return next
}

export function sortFromParams(params: URLSearchParams): { sort: DocSortKey; order: DocSortOrder } {
  const rawSort = params.get("sort") as DocSortKey | null
  const rawOrder = params.get("order")
  return {
    sort: rawSort && SORT_KEYS.includes(rawSort) ? rawSort : DEFAULT_SORT,
    order: rawOrder === "desc" ? "desc" : DEFAULT_ORDER
  }
}

/** All available topics (from the list response topics). */
export function extractTopics(topics: { topic: string; documents: unknown[] }[]): string[] {
  return topics.map((t) => t.topic).filter((t) => t).sort()
}
