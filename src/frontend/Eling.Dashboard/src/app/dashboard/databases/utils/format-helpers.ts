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
