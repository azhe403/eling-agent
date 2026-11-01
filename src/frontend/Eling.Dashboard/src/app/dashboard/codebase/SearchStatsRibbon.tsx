"use client"

import { Zap, Clock, FileText } from "lucide-react"
import type { SearchStats } from "./searchTabs"
import { SEARCH_SCOPE_COPY, type SearchScope } from "./scope"

interface SearchStatsRibbonProps {
  stats: SearchStats
  scope: SearchScope
  limit: number
  onLimitChange?: (limit: number) => void
}

/**
 * Modern segmented ribbon bar displayed under the query input showing
 * execution duration, matches, files, chunks, and workspace coverage for the active tab.
 */
export function SearchStatsRibbon({
  stats,
  scope,
  limit,
  onLimitChange,
}: SearchStatsRibbonProps) {
  const scopeLabel = SEARCH_SCOPE_COPY[scope]?.label ?? scope
  const isTopMode = stats.mode === "top"
  const hasMatches = stats.hits > 0

  return (
    <div
      role="status"
      aria-label="Search statistics"
      className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1.5 rounded-lg border border-border/60 bg-muted/30 px-3 py-1.5 text-xs transition-colors"
    >
      <div className="flex flex-wrap items-center gap-2.5">
        {/* Accent match pill */}
        {isTopMode ? (
          <div className="inline-flex items-center gap-1.5 rounded-md border border-sky-500/25 bg-sky-500/10 px-2 py-0.5 text-xs font-semibold tracking-tight text-sky-400">
            <FileText className="size-3 shrink-0" />
            <span>Recent files</span>
          </div>
        ) : (
          <div
            className={`inline-flex items-center gap-1.5 rounded-md px-2 py-0.5 text-xs font-semibold tracking-tight ${
              hasMatches
                ? "border border-emerald-500/25 bg-emerald-500/10 text-emerald-400"
                : "border border-muted-foreground/20 bg-muted/60 text-muted-foreground"
            }`}
          >
            <Zap className="size-3 shrink-0" />
            <span>
              {stats.hits === 0
                ? "No matches"
                : stats.totalHits && stats.totalHits > stats.hits
                ? `${stats.hits} of ${stats.totalHits} matches`
                : `${stats.hits} ${stats.hits === 1 ? "match" : "matches"}`}
            </span>
          </div>
        )}

        <div className="h-3.5 w-px bg-border/60" aria-hidden="true" />

        {/* Files & Chunks */}
        <div className="inline-flex items-center gap-1.5 text-foreground/90">
          <span className="font-semibold text-foreground">{stats.files}</span>
          <span className="text-muted-foreground">
            {stats.files === 1 ? "file" : "files"}
          </span>
          <span className="text-border" aria-hidden="true">
            ·
          </span>
          <span className="font-semibold text-foreground">{stats.chunks}</span>
          <span className="text-muted-foreground">
            {stats.chunks === 1 ? "chunk" : "chunks"}
          </span>
        </div>

        <div className="h-3.5 w-px bg-border/60" aria-hidden="true" />

        {/* Workspaces */}
        <div className="inline-flex items-center gap-1.5 text-foreground/90">
          <span className="font-semibold text-foreground">{stats.workspaces}</span>
          <span className="text-muted-foreground">
            {stats.workspaces === 1 ? "workspace" : "workspaces"}
          </span>
          {stats.workspacesWithHits > 0 && (
            <span
              className="inline-flex items-center rounded border border-emerald-500/20 bg-emerald-500/10 px-1.5 py-0.2 text-[10px] font-medium text-emerald-400"
              title={`${stats.workspacesWithHits} workspace(s) represented`}
            >
              {stats.workspacesWithHits}{" "}
              {isTopMode ? "active" : stats.workspacesWithHits === 1 ? "hit" : "hits"}
            </span>
          )}
        </div>

        <div className="h-3.5 w-px bg-border/60" aria-hidden="true" />

        {/* Execution duration */}
        <div className="inline-flex items-center gap-1 rounded bg-muted/60 px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
          <Clock className="size-3 text-muted-foreground/70" />
          <span>{stats.tookMs}ms</span>
        </div>
      </div>

      {/* Scope info & interactive limit selector */}
      <div className="flex items-center gap-2 text-[11px] text-muted-foreground">
        <span className="font-medium text-foreground/80">
          {isTopMode ? "Recent" : scopeLabel}
        </span>
        <span className="text-border" aria-hidden="true">
          ·
        </span>
        <div className="flex items-center gap-1">
          <span className="text-[10px] text-muted-foreground">limit:</span>
          <div className="inline-flex rounded-md border border-border/50 bg-background/50 p-0.5">
            {[20, 50, 100].map((val) => (
              <button
                key={val}
                type="button"
                onClick={() => onLimitChange?.(val)}
                className={`rounded px-1.5 py-0.2 text-[10px] font-medium transition-colors ${
                  limit === val
                    ? "bg-primary text-primary-foreground shadow-xs"
                    : "text-muted-foreground hover:bg-muted/80 hover:text-foreground"
                }`}
                title={`Set search limit to ${val}`}
              >
                {val}
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
