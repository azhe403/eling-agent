/**
 * The identity of an open tab in the databases explorer.
 *
 * A SQL tab is a connection, a logical database and a table. A Redis tab is a
 * connection and a logical Redis database - the key tree behind it is not a
 * table, it is whatever that database holds. Both kinds live in one tab bar,
 * so the tab that holds the active view has to be recognisable.
 */

/** The table name a Redis tab carries. It is a label, not a table. */
export const REDIS_TAB_TABLE = "Redis keys"

import type { BrowseFilter, BrowseSort } from "./filter-model"
import { sanitizeBrowseFilters, sanitizeBrowseSort, sanitizeManualCondition } from "./filter-model"
import { esTabKey } from "./es-browser"

export type DbTab = {
  connId: string
  table: string
  database?: string | null
  kind?: "redis" | "elasticsearch-index"
  /** The logical Redis database this tab shows. Only set for a Redis tab. */
  redisDb?: number
  /** The Elasticsearch index this tab shows. Only set for an Elasticsearch tab. */
  index?: string
  /**
   * How many rows the Data tab fetches. Session view state: lives on the tab
   * so switching tabs keeps each tab's choice, never persisted.
   */
  browseLimit?: number
  /** The 1-based page of the Data tab. Session view state, like browseLimit. */
  browsePage?: number
  /** The committed conditions of the Data tab. Session view state, like browseLimit. */
  browseFilters?: BrowseFilter[]
  /** The committed sort columns of the Data tab. Session view state, like browseLimit. */
  browseSort?: BrowseSort[]
  /** The committed raw manual WHERE fragment, ANDed verbatim. Session view state. */
  manualCondition?: string
  /** Whether the manual condition participates in /browse. Absent = enabled. */
  manualEnabled?: boolean
  /** Whether the filter panel is open in this tab. Persisted with the tab. */
  filterOpen?: boolean
}

/**
 * Whether a tab is a Redis key browser.
 *
 * The table name is checked as well as the kind: a tab restored from an URL
 * carries only the name, because the kind lives in the saved state. Treating
 * that as a plain table sends it to the SQL endpoint, which answers 400 for a
 * Redis connection.
 */
export const isRedisTab = (tab?: { table?: string; kind?: string } | null): boolean =>
  tab?.kind === "redis" || tab?.table === REDIS_TAB_TABLE

/**
 * Whether a tab is an Elasticsearch index.
 *
 * The kind is the whole answer: an Elasticsearch tab restored from localStorage
 * is still one, and must not fall through to the SQL path (which the backend
 * refuses for Elasticsearch connections).
 */
export const isElasticsearchIndexTab = (tab?: { table?: string; kind?: string } | null): boolean =>
  tab?.kind === "elasticsearch-index"

/**
 * The identity of a tab: a Redis tab by connection and database, an
 * Elasticsearch tab by connection and index, and a SQL tab by connection,
 * database and table.
 *
 * Two databases of one server are two different views and must be two
 * different tabs; the same holds for two indices of one cluster.
 */
export const tabKey = (tab: DbTab): string =>
  isElasticsearchIndexTab(tab)
    ? esTabKey(tab.connId, tab.index ?? "")
    : isRedisTab(tab)
      ? redisTabKey(tab)
      : `sql:${tab.connId}:${tab.database ?? ""}:${tab.table}`

export const redisTabKey = (tab: DbTab): string => `${tab.connId}:${tab.redisDb ?? 0}`

/**
 * The persisted form of a tab: identity + its committed filter/sort/manual-condition.
 * `browseLimit` and `browsePage` are excluded on purpose (a resurrected page 9 of an
 * empty filtered table is a trap). `filterOpen` persists so an open filter stays open.
 */
export const persistableTab = (tab: DbTab): DbTab => {
  const { browseLimit: _browseLimit, browsePage: _browsePage, ...rest } = tab
  return rest
}

/** A clean DbTab from a historical localStorage payload, or null if the tab is not usable. */
export const sanitizePersistedTab = (t: unknown): DbTab | null => {
  if (typeof t !== "object" || t === null) return null
  const raw = t as Record<string, unknown>
  const connId = typeof raw.connId === "string" ? raw.connId : null
  const table = typeof raw.table === "string" && raw.table.length > 0 ? raw.table : null
  if (!connId || !table) return null

  const tab: DbTab = { connId, table }
  if (typeof raw.database === "string") tab.database = raw.database
  if (raw.kind === "redis") tab.kind = "redis"
  if (typeof raw.redisDb === "number") tab.redisDb = raw.redisDb
  if (raw.kind === "elasticsearch-index" && typeof raw.index === "string" && raw.index.length > 0) {
    tab.kind = "elasticsearch-index"
    tab.index = raw.index
  }
  // Session view state: keep whatever the reader committed before the reload.
  tab.browseFilters = sanitizeBrowseFilters(raw.browseFilters)
  tab.browseSort = sanitizeBrowseSort(raw.browseSort)
  tab.manualCondition = sanitizeManualCondition(raw.manualCondition)
  if (raw.manualEnabled === false) tab.manualEnabled = false
  if (raw.filterOpen === true) tab.filterOpen = true
  return tab
}
