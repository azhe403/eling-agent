"use client"

import { Zap, Clock, FileText } from "lucide-react"
import type { SearchStats } from "./searchTabs"
import type { SearchScope } from "./scope"

interface SearchStatsRibbonProps {
  stats: SearchStats
  scope: SearchScope
  limit: number
  onLimitChange?: (limit: number) => void
}

/**
 * The limit options are a 1-2-5 geometric ladder rendered in full — no
 * sliding window — so lowering the limit is always a single click. The ladder
 * extends past 200 on demand to a round number covering the current match
 * count — a query with 1387 matches gets options all the way up to 2000. The
 * ladder is capped at LADDER_MAX (20000) to keep the payload bounded.
 */
const LADDER_MAX = 20000

function buildLimitLadder(cover: number): number[] {
  const out: number[] = []
  const ceiling = Math.min(Math.max(cover, 200), LADDER_MAX)
  let n = 5
  while (true) {
    out.push(n)
    if (n >= ceiling) break
    n = out.length % 3 === 0 ? Math.round(n * 2.5) : n * 2
  }
  return out
}

export function limitOptions(current: number, cover: number): readonly number[] {
  return buildLimitLadder(Math.max(cover, current, 200))
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
  const isTopMode = stats.mode === "top"
  const hasMatches = stats.hits > 0
  // Every group gets its own boundary so the segments read as separate units.
  const groupBox =
    "inline-flex h-8 items-center gap-1.5 rounded-md border border-border/50 bg-background/50 px-2"

  return (
    <div
      role="status"
      aria-label="Search statistics"
      className="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-lg border border-border/60 bg-muted/30 px-3 py-1.5 text-xs transition-colors"
    >
      <div className="flex flex-wrap items-center gap-2.5">
        {/* Accent match pill */}
        {isTopMode ? (
          <div className="inline-flex h-8 items-center gap-1.5 rounded-md border border-sky-500/25 bg-sky-500/10 px-2 text-xs font-semibold tracking-tight text-sky-400">
            <FileText className="size-3 shrink-0" />
            <span>Recent files</span>
          </div>
        ) : (
          <div
            className={`inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-xs font-semibold tracking-tight ${
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

        {/* Files & Chunks */}
        <div className={`${groupBox} text-foreground/90`}>
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

        {/* Workspaces */}
        <div className={`${groupBox} text-foreground/90`}>
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

        {/* Execution duration */}
        <div className={`${groupBox} font-mono text-[11px] text-muted-foreground`}>
          <Clock className="size-3 text-muted-foreground/70" />
          <span>{stats.tookMs}ms</span>
        </div>

        {/* Max results — how many hits/files the list shows */}
        <div className={`${groupBox} text-xs text-muted-foreground`}>
          <span className="text-[11px] font-medium text-foreground/60">Max</span>
          <div className="inline-flex rounded-md border border-border/50 bg-background/50 p-0.5">
              {limitOptions(limit, stats.totalHits ?? stats.hits).map((val) => (
                <button
                  key={val}
                  type="button"
                  onClick={() => onLimitChange?.(val)}
                  className={`min-w-8 rounded px-2 py-1 text-xs font-medium transition-colors ${
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
