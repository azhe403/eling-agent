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
  keys: Array<{
    key: string
    type: string
    ttlSeconds: number
    /** When the key dies, as an absolute moment. Absent when it has no expiry. */
    expiresAt?: string | null
    /** True when a recheck found the key gone; the row stays, marked. */
    gone?: boolean
    /**
     * True when the backend's page cache carried this row over because the key
     * is no longer in the keyspace. The row stays in the tree instead of
     * disappearing on its own.
     */
    expired?: boolean
  }>
  cursor: number
  pageOffset: number
  complete: boolean
  scanned: number
  /** True when the server was unreachable and the page came from the read cache. */
  fromCache?: boolean
  /** When the cached page was recorded; shown as "cached as of" next to the counts. */
  cachedAt?: string | null
}

export type RedisTtlMode = "countdown" | "datetime"

/**
 * The TTL of one key, as the row shows it.
 *
 * Two shapes, because two questions get asked of a TTL: "how long is left"
 * (the countdown) and "when does it die" (the moment). The countdown ticks
 * down every second from the absolute expiry, so it never sits still on the
 * value the server happened to answer with; the moment is that expiry
 * written out, which does not move and does not need to.
 *
 * A key without an expiry keeps Redis's own -1, and says "no ttl". A key
 * whose expiry has passed says "expired" - the recheck decides whether it is
 * really gone.
 */
export const redisTtlLabel = (
  key: { ttlSeconds: number; expiresAt?: string | null },
  now: number,
  mode: RedisTtlMode,
): string => {
  const expiresMs = key.expiresAt ? new Date(key.expiresAt).getTime() : Number.NaN
  const hasExpiry = Number.isFinite(expiresMs)

  if (!hasExpiry) {
    // Redis answers -1 for a key without an expiry and 0 for one that is
    // already gone; neither counts down.
    if (key.ttlSeconds <= 0) return key.ttlSeconds === 0 ? "expired" : "no ttl"
    const secondsLeft = Math.floor(key.ttlSeconds)
    if (mode === "datetime") return new Date(now + secondsLeft * 1000).toLocaleString()
    return formatClock(secondsLeft)
  }

  if (mode === "datetime") {
    return new Date(expiresMs).toLocaleString("en-US", {
      month: "short",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      hour12: false,
    })
  }

  const secondsLeft = Math.max(0, Math.floor((expiresMs - now) / 1000))
  if (secondsLeft <= 0) return "expired"
  return formatClock(secondsLeft)
}

/** A countdown as hours, minutes and seconds: `2h 14m 03s`. */
const formatClock = (seconds: number): string => {
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  return `${h}h ${m}m ${String(s).padStart(2, "0")}s`
}

/** The answer to deleting one Redis key from the dashboard: the key is gone. */
export type RedisKeyDeleteResult = { deleted: boolean }

/**
 * What a recheck found for one key: whether it is still there, and its TTL.
 *
 * A key whose countdown reached zero either died or was rewritten with a new
 * expiry, and only the server knows which.
 */
export type RedisKeyProbe = {
  key: string
  exists: boolean
  ttlSeconds: number
  expiresAt: string | null
}

/**
 * Asks the server whether a key is still there.
 *
 * One request per key whose countdown just ran out, never a poll.
 */
export const fetchRedisKeyProbe = async (
  connId: string,
  database: number,
  key: string,
): Promise<RedisKeyProbe> => {
  const qs = new URLSearchParams()
  qs.set("database", String(database))
  qs.set("key", key)
  const response = await fetch(`${API}/${connId}/keys/probe?${qs.toString()}`)
  if (!response.ok) throw new Error(parseApiError(await response.text()))
  return parseProbe(await response.json(), key)
}

/**
 * The same question for many keys, in one request.
 *
 * Keys that expire together - a batch job finishing, a queue draining - would
 * otherwise be one round trip each, all at the same instant. A key the answer
 * leaves out is a key that is gone.
 */
export const fetchRedisKeyProbeBatch = async (
  connId: string,
  database: number,
  keys: string[],
): Promise<RedisKeyProbe[]> => {
  if (keys.length === 0) return []
  const response = await fetch(`${API}/${connId}/keys/probe`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ database, keys }),
  })
  if (!response.ok) throw new Error(parseApiError(await response.text()))
  const body = await response.json()
  if (!Array.isArray(body)) return []
  return body.map((entry) => parseProbe(entry, typeof entry?.key === "string" ? entry.key : ""))
}

const parseProbe = (raw: unknown, fallbackKey: string): RedisKeyProbe => {
  const body = (raw ?? {}) as {
    key?: unknown
    exists?: unknown
    ttlSeconds?: unknown
    expiresAt?: unknown
  }
  return {
    key: typeof body.key === "string" ? body.key : fallbackKey,
    exists: body.exists === true,
    ttlSeconds: typeof body.ttlSeconds === "number" ? body.ttlSeconds : -1,
    expiresAt: typeof body.expiresAt === "string" ? body.expiresAt : null,
  }
}

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
 *
 * Cache-first like the schema read: the first call paints the snapshot at
 * once and the caller revalidates with `refresh: true` to ask the server for
 * the real answer. An explicit refresh skips the cache.
 */
export const fetchRedisDatabases = async (connId: string, refresh?: boolean): Promise<RedisDbInfo[]> => {
  const qs = refresh ? "?refresh=true" : ""
  const response = await fetch(`/api/databases/${connId}/keys/databases${qs}`)
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

/**
 * How the counts line says a cached page is not live.
 *
 * The scan cache is an offline fallback, like the database list: a page
 * served while the server is unreachable carries the moment it was taken so
 * the reader knows the tree is a snapshot, not the current keyspace. Null
 * when the page is live, which keeps the counts quiet in the normal case.
 */
export const pageAsOf = (page: RedisPage | null | undefined): string | null => {
  if (!page?.fromCache) return null
  if (!page.cachedAt) return "cached"
  const when = new Date(page.cachedAt)
  if (Number.isNaN(when.getTime())) return "cached"
  return `cached as of ${when.toLocaleString()}`
}
