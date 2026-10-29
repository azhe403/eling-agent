/**
 * The Redis key browser: the logical databases of a connection, and the
 * decisions the dashboard makes about which one to show.
 *
 * Everything here is pure apart from `fetchRedisDatabases`, so the rules that
 * decide "which database does this URL mean" can be tested without a browser
 * and without a Redis server.
 */

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
