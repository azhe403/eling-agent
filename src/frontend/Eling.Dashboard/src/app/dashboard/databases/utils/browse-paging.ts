/**
 * The Data tab's paging arithmetic: how many rows a tab fetches, which page
 * it is on, and what a freshly opened tab should inherit.
 *
 * A tab owns its `browseLimit` and `browsePage` (see DbTab), so these helpers
 * take a tab (or the open set) rather than global state. The defaults are
 * deliberately boring: 100 rows, page 1, anything unrecognized falls back to
 * those rather than throwing - a hand-edited localStorage entry must not
 * take the grid down.
 */

import { isRedisTab, type DbTab } from "../db-tabs"

/** The page sizes the dropdown offers. */
export const BROWSE_LIMIT_OPTIONS = [50, 100, 250, 500, 1000] as const

/** What a tab fetches when it carries no limit of its own. */
export const DEFAULT_BROWSE_LIMIT = 100

/** The tab's limit, or 100 for missing/garbage values. */
export const effectiveBrowseLimit = (tab: DbTab | null | undefined): number => {
  const v = tab?.browseLimit
  return typeof v === "number" && (BROWSE_LIMIT_OPTIONS as readonly number[]).includes(v)
    ? v
    : DEFAULT_BROWSE_LIMIT
}

/** The tab's 1-based page, or 1 for missing/garbage values. */
export const effectiveBrowsePage = (tab: DbTab | null | undefined): number => {
  const v = tab?.browsePage
  return typeof v === "number" && Number.isFinite(v) && v >= 1 ? Math.floor(v) : 1
}

/**
 * The default for a newly opened table tab: the mode (most frequent value) of
 * the effective limits of the open table tabs. Redis tabs do not count. Ties
 * go to the larger value; with no table tabs open the answer is 100.
 */
export const defaultBrowseLimit = (openTabs: DbTab[]): number => {
  const counts = new Map<number, number>()
  for (const t of openTabs) {
    if (isRedisTab(t)) continue
    const v = effectiveBrowseLimit(t)
    counts.set(v, (counts.get(v) ?? 0) + 1)
  }
  let best = DEFAULT_BROWSE_LIMIT
  let bestCount = 0
  for (const [value, count] of counts) {
    if (count > bestCount || (count === bestCount && value > best)) {
      best = value
      bestCount = count
    }
  }
  return best
}

/** How many pages a total spans; null when the total is unknown. */
export const totalPages = (total: number | null, limit: number): number | null =>
  total === null ? null : total <= 0 ? 0 : Math.ceil(total / limit)

/** The page to show for a request that named `page`: valid pages pass, out-of-range clamps to the last (or 1). */
export const clampPage = (page: number, total: number | null, limit: number): number => {
  const p = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1
  const pages = totalPages(total, limit)
  if (pages === null || pages <= 0) return 1
  return Math.min(p, pages)
}
