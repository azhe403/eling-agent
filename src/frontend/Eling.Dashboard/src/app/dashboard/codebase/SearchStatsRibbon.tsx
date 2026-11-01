"use client"

import { Zap, FileText, Layers, Clock, Globe } from "lucide-react"
import type { SearchStats } from "./searchTabs"
import { SEARCH_SCOPE_COPY, type SearchScope } from "./scope"

interface SearchStatsRibbonProps {
  stats: SearchStats
  scope: SearchScope
  limit: number
}

/**
 * Compact ribbon bar displayed under the query input showing execution
 * duration, matches, files, chunks, and workspace coverage for the active tab.
 */
export function SearchStatsRibbon({
  stats,
  scope,
  limit,
}: SearchStatsRibbonProps) {
  const scopeLabel = SEARCH_SCOPE_COPY[scope]?.label ?? scope

  return (
    <div
      role="status"
      aria-label="Search statistics"
      className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md border border-primary/20 bg-primary/5 px-2.5 py-1.5 text-xs text-muted-foreground"
    >
      <div className="flex items-center gap-1 font-medium text-primary">
        <Zap className="size-3.5 shrink-0" />
        <span>
          {stats.hits === 0
            ? "No matches"
            : `${stats.hits} ${stats.hits === 1 ? "match" : "matches"}`}
        </span>
      </div>

      <span className="text-border" aria-hidden="true">
        ·
      </span>

      <div className="flex items-center gap-1 text-foreground">
        <FileText className="size-3.5 shrink-0 text-muted-foreground" />
        <span>
          <strong>{stats.files}</strong> {stats.files === 1 ? "file" : "files"}
        </span>
      </div>

      <span className="text-border" aria-hidden="true">
        ·
      </span>

      <div className="flex items-center gap-1 text-foreground">
        <Layers className="size-3.5 shrink-0 text-muted-foreground" />
        <span>
          <strong>{stats.chunks}</strong> {stats.chunks === 1 ? "chunk" : "chunks"}
        </span>
      </div>

      <span className="text-border" aria-hidden="true">
        ·
      </span>

      <div className="flex items-center gap-1 text-foreground">
        <Clock className="size-3.5 shrink-0 text-muted-foreground" />
        <span>
          <strong>{stats.tookMs}</strong> ms
        </span>
      </div>

      <span className="text-border" aria-hidden="true">
        ·
      </span>

      <div className="flex items-center gap-1">
        <Globe className="size-3.5 shrink-0 text-muted-foreground" />
        <span>
          <strong>{stats.workspaces}</strong>{" "}
          {stats.workspaces === 1 ? "workspace" : "workspaces"}
          {stats.workspaces > 1 && stats.workspacesWithHits > 0 && (
            <span className="ml-1 text-muted-foreground">
              ({stats.workspacesWithHits} with hits)
            </span>
          )}
        </span>
      </div>

      <div className="ml-auto flex items-center text-[11px] text-muted-foreground">
        <span>
          Scope: {scopeLabel} (top {limit})
        </span>
      </div>
    </div>
  )
}
