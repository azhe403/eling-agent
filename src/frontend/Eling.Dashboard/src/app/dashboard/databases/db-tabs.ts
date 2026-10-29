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

export type DbTab = {
  connId: string
  table: string
  database?: string | null
  kind?: "redis"
  /** The logical Redis database this tab shows. Only set for a Redis tab. */
  redisDb?: number
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
 * The identity of a Redis tab: the connection and the database.
 *
 * Two databases of one connection are two different views and must be two
 * different tabs.
 */
export const tabKey = (tab: DbTab): string =>
  isRedisTab(tab) ? redisTabKey(tab) : `sql:${tab.connId}:${tab.database ?? ""}:${tab.table}`

export const redisTabKey = (tab: DbTab): string => `${tab.connId}:${tab.redisDb ?? 0}`
