/**
 * Format helpers: cache ages and stale checks.
 */

/**
 * How long ago a cached value was taken, in words.
 *
 * A bare timestamp says nothing at a glance; "4 min ago" does.
 */
export const formatCacheAge = (cachedAt?: string | null): string => {
  if (!cachedAt) return "cached"
  const ms = Date.now() - new Date(cachedAt).getTime()
  if (!Number.isFinite(ms) || ms < 0) return "cached"
  const sec = Math.floor(ms / 1000)
  if (sec < 5) return "just now"
  if (sec < 60) return `${sec}s ago`
  const min = Math.floor(sec / 60)
  if (min < 60) return `${min} min ago`
  const hr = Math.floor(min / 60)
  if (hr < 24) return `${hr} h ago`
  const day = Math.floor(hr / 24)
  return `${day} day${day === 1 ? "" : "s"} ago`
}

/** True when a cached value is older than a day. */
export const isStaleCache = (cachedAt?: string | null): boolean => {
  if (!cachedAt) return false
  const ms = Date.now() - new Date(cachedAt).getTime()
  return Number.isFinite(ms) && ms > 24 * 60 * 60 * 1000
}

/**
 * The two facts a cache badge shows: how old the answer is and how long the
 * snapshot still has. A response that carries no timestamp was fetched on
 * this render, so it is "just now" with the default TTL - never "no cache":
 * database reads are cached, a live answer simply has not been served from
 * the snapshot yet.
 */
export const cacheBadgeCopy = (cachedAt?: string | null, ttlSeconds?: number | null) => {
  const stamp = cachedAt ?? new Date().toISOString()
  return {
    age: formatCacheAge(stamp),
    expiry: formatCacheExpiry(stamp, ttlSeconds),
  }
}

/**
 * A column's type with its declared length, "varchar" and 50 giving
 * "varchar(50)". A type that already carries the length in its text - SQLite
 * reports the whole declared type as one string - is left alone rather than
 * given a second copy, and a column with no declared type stays blank instead
 * of showing a length on its own.
 */
export const formatColumnType = (dataType?: string | null, maxLength?: number | null): string => {
  const type = dataType ?? ""
  if (!type || !maxLength || maxLength <= 0 || type.includes("(")) return type
  return `${type}(${maxLength})`
}

/**
 * Format cache expiration time for display. Returns "expires in X" or "expired".
 * The TTL is typically 7 days from cachedAt.
 */
export const formatCacheExpiry = (cachedAt?: string | null, ttlSeconds?: number | null): string => {
  if (!cachedAt) return ""
  const cached = new Date(cachedAt).getTime()
  if (!Number.isFinite(cached)) return ""
  const ttl = ttlSeconds ?? 7 * 24 * 60 * 60 // default 7 days
  const expiresAt = cached + ttl * 1000
  const now = Date.now()
  const diff = expiresAt - now
  if (diff <= 0) return "expired"
  const days = Math.floor(diff / (24 * 60 * 60 * 1000))
  const hours = Math.floor((diff % (24 * 60 * 60 * 1000)) / (60 * 60 * 1000))
  const mins = Math.floor((diff % (60 * 60 * 1000)) / (60 * 1000))
  if (days > 0) return `expires in ${days}d ${hours}h`
  if (hours > 0) return `expires in ${hours}h ${mins}m`
  return `expires in ${mins}m`
}
