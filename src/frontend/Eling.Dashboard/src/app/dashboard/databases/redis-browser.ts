/**
 * The Redis key browser: the logical databases of a connection, and the
 * decisions the dashboard makes about which one to show.
 *
 * Everything here is pure apart from `fetchRedisDatabases`, so the rules that
 * decide "which database does this URL mean" can be tested without a browser
 * and without a Redis server.
 */

import { API, parseApiError } from "./utils/connection-helpers"

/** One logical Redis database, as the server reports it. */
export type RedisDbInfo = {
  index: number
  keys: number
  /** True when the server was unreachable and the count came from the read cache. */
  fromCache?: boolean
  /** When the cached count was recorded; shown as "as of" next to the row. */
  cachedAt?: string | null
}

/**
 * One page of the lazy key tree: the prefixes and keys directly under one
 * prefix, plus the cursor that continues it.
 */
export type RedisPage = {
  prefix: string
  nodes: Array<{ segment: string; prefix: string }>
  keys: Array<{ key: string; type: string; ttlSeconds: number }>
  cursor: number
  pageOffset: number
  complete: boolean
  scanned: number
}

/** The answer to deleting one Redis key from the dashboard: the key is gone. */
export type RedisKeyDeleteResult = { deleted: boolean }

/**
 * The flat list of keys under one prefix, for the subtree delete dialog: the
 * capped list the operator confirms against, the full count, and whether the
 * scan budget cut it short.
 */
export type RedisPrefixPreview = {
  prefix: string
  keys: string[]
  total: number
  truncated: boolean
}

/** The answer to deleting one prefix subtree: how many keys actually went. */
export type RedisPrefixDeleteResult = { deleted: number; truncated: boolean }

/** The name of a database in the tree. */
export const dbRowLabel = (db: RedisDbInfo): string => `db${db.index}`

/**
 * How many keys a database holds.
 *
 * The unit is spelled out: a bare number next to a database name reads as a
 * database number, not a count.
 */
export const dbRowCount = (db: RedisDbInfo): string => `${db.keys} keys`

/**
 * The database index an URL asks for, or null when it does not ask for one
 * that can be used.
 *
 * Redis has sixteen logical databases, so an index outside 0-15 names
 * nothing.
 */
export const parseRedisDb = (raw: string | null | undefined): number | null => {
  if (raw === null || raw === undefined) return null
  const value = Number.parseInt(raw, 10)
  if (!Number.isInteger(value)) return null
  if (value < 0 || value > 15) return null
  return value
}

/**
 * Which database to show.
 *
 * The URL wins, so a copied link lands on the same database. Then the saved
 * tab - but only when that tab belongs to the same connection: the explorer
 * state is one localStorage key for the whole application, and a second tab
 * on another connection would otherwise leak its database in here. Then the
 * first database the server reports.
 */
export const pickRedisDb = (opts: {
  urlDb: string | null
  savedTab?: { connId: string; redisDb?: number } | null
  connId: string
  available: RedisDbInfo[]
}): number => {
  const fromUrl = parseRedisDb(opts.urlDb)
  if (fromUrl !== null) return fromUrl

  const saved = opts.savedTab
  if (saved && saved.connId === opts.connId && typeof saved.redisDb === "number") {
    if (Number.isInteger(saved.redisDb) && saved.redisDb >= 0 && saved.redisDb <= 15) {
      return saved.redisDb
    }
  }

  return opts.available[0]?.index ?? 0
}

/**
 * The databases to draw in the tree: the ones the server reported, plus the
 * active one when it is missing, so a link to an empty database still shows
 * where it is.
 */
export const visibleRedisDbs = (
  dbs: RedisDbInfo[],
  activeDb: number | null | undefined,
): RedisDbInfo[] => {
  const result = [...dbs]
  if (activeDb !== null && activeDb !== undefined && !result.some((d) => d.index === activeDb)) {
    result.push({ index: activeDb, keys: 0 })
  }
  return result.sort((a, b) => a.index - b.index)
}

/**
 * The cache key for one page of one database's tree.
 *
 * The connection belongs in the key: two Redis connections both have a db0, and
 * without the id the second connection's tree reuses the first one's page
 * instead of loading its own.
 */
export const redisPageKey = (connId: string, db: number, prefix: string): string =>
  `${connId}:${db}:${prefix || "(root)"}`

/**
 * The cache-key prefix shared by every page of one database's tree. Clearing a
 * database means dropping the entries that start with this.
 */
export const redisPageScope = (connId: string, db: number): string => `${connId}:${db}:`

/**
 * The logical databases of a Redis connection.
 */
export const fetchRedisDatabases = async (connId: string): Promise<RedisDbInfo[]> => {
  const response = await fetch(`/api/databases/${connId}/keys/databases`)
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text.trim() || `could not read the databases of ${connId}`)
  }
  const body = await response.json()
  if (!Array.isArray(body)) return []
  return body.map((entry) => ({
    index: Number(entry?.index ?? 0),
    keys: Number(entry?.keys ?? 0),
    fromCache: Boolean(entry?.fromCache),
    cachedAt: typeof entry?.cachedAt === "string" ? entry.cachedAt : null,
  }))
}

/**
 * How the tree row says a cached count is not live.
 *
 * A bare number that is two days old looks exactly like one that is current,
 * so a cached count carries the moment it was taken. Null when the count is
 * live, which keeps the tree quiet in the normal case.
 */
export const dbRowAsOf = (db: RedisDbInfo): string | null => {
  if (!db.fromCache) return null
  if (!db.cachedAt) return "cached"
  const when = new Date(db.cachedAt)
  if (Number.isNaN(when.getTime())) return "cached"
  return `as of ${when.toLocaleString()}`
}

/**
 * Delete one Redis key.
 *
 * The key travels in the query string, never as a path segment: keys may
 * contain `/`, `:` or `%` and would split the route.
 */
export const fetchRedisKeyDelete = async (
  connId: string,
  database: number,
  key: string,
): Promise<RedisKeyDeleteResult> => {
  const qs = new URLSearchParams()
  qs.set("database", String(database))
  qs.set("key", key)
  const response = await fetch(`${API}/${connId}/keys/delete?${qs.toString()}`, { method: "DELETE" })
  if (!response.ok) throw new Error(parseApiError(await response.text()))
  const body = (await response.json()) as { deleted?: unknown }
  return { deleted: body?.deleted === true }
}

/**
 * The flat list of keys under one prefix, for the subtree delete dialog.
 */
export const fetchRedisPrefixPreview = async (
  connId: string,
  database: number,
  prefix: string,
): Promise<RedisPrefixPreview> => {
  const qs = new URLSearchParams()
  qs.set("database", String(database))
  qs.set("prefix", prefix)
  const response = await fetch(`${API}/${connId}/keys/preview?${qs.toString()}`)
  if (!response.ok) throw new Error(parseApiError(await response.text()))
  const body = (await response.json()) as {
    prefix?: unknown
    keys?: unknown
    total?: unknown
    truncated?: unknown
  }
  return {
    prefix: typeof body?.prefix === "string" ? body.prefix : prefix,
    keys: Array.isArray(body?.keys)
      ? body.keys.filter((k): k is string => typeof k === "string")
      : [],
    total: typeof body?.total === "number" ? body.total : 0,
    truncated: body?.truncated === true,
  }
}

/**
 * Delete every key under one prefix subtree.
 */
export const fetchRedisPrefixDelete = async (
  connId: string,
  database: number,
  prefix: string,
): Promise<RedisPrefixDeleteResult> => {
  const qs = new URLSearchParams()
  qs.set("database", String(database))
  qs.set("prefix", prefix)
  const response = await fetch(`${API}/${connId}/keys/prefix?${qs.toString()}`, { method: "DELETE" })
  if (!response.ok) throw new Error(parseApiError(await response.text()))
  const body = (await response.json()) as { deleted?: unknown; truncated?: unknown }
  return {
    deleted: typeof body?.deleted === "number" ? body.deleted : 0,
    truncated: body?.truncated === true,
  }
}
