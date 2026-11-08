"use client"

import { useCallback, useMemo } from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import {
  queryParam,
  searchScopeQuery,
  selectionQuery,
  tabParam,
  type SearchScope,
} from "./scope"
import type { WorkspaceSelection } from "@/components/workspaces/selection"
import type { SelectedFile } from "./fileDetailApi"

// The open file's URL identity. Two params rather than one encoded pair: both
// values are paths, and unmerged they stay readable in the address bar and in
// a shared link. `line` jumps the viewer straight to a search hit's line.
export function fileQuery(file: SelectedFile): string {
  const parts = [
    `file=${encodeURIComponent(file.path)}`,
    `fileRoot=${encodeURIComponent(file.projectRoot)}`,
  ]
  if (file.startLine) parts.push(`line=${file.startLine}`)
  return parts.join("&")
}

/** The open file, read back out of the URL. */
export function fileFromParams(params: URLSearchParams): SelectedFile | null {
  const path = params.get("file")?.trim()
  const root = params.get("fileRoot")?.trim()
  const line = params.get("line")?.trim()
  return path && root
    ? {
        projectRoot: root,
        path,
        ...(line ? { startLine: Number(line) } : {}),
      }
    : null
}

/** Everything this page keeps in the address bar, in one piece. */
export interface CodebaseUrlState {
  /** The tab the page is on. */
  tab: string
  selection: WorkspaceSelection
  scope: SearchScope
  query: string
  /** The open file, or null when the viewer is closed. */
  file: SelectedFile | null
}

/**
 * The page's state in the address bar.
 *
 * One writer for all of it, so changing one piece never drops another: a
 * scope change keeps the query and the open file, closing the file keeps the
 * scope, and a new query keeps the scope. Reload, back/forward and a shared
 * link all land on the same view, because everything the reader can change
 * lives here.
 *
 * The tab is part of every write, so the writer never has to know which tab
 * is active - the caller says which one it is writing for.
 */
export function useCodebaseUrlState() {
  const searchParams = useSearchParams()
  const router = useRouter()
  const pathname = usePathname()

  /** The open file. */
  const selectedFile = useMemo(() => fileFromParams(searchParams), [searchParams])

  /** Writes every piece of this page's URL state at once. */
  const writeUrl = useCallback(
    (next: CodebaseUrlState) => {
      const parts = [
        tabParam(next.tab),
        selectionQuery(next.selection),
        searchScopeQuery(next.scope),
        queryParam(next.query),
        next.file ? fileQuery(next.file) : "",
      ].filter(Boolean)
      const qs = parts.join("&")
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [pathname, router]
  )

  return {
    searchParams,
    selectedFile,
    writeUrl,
  }
}
