// UTC -> the reader's local timezone. Display only: the API contract is UTC
// everywhere, and this never mutates or reformats the wire value.
//
// Every timestamp arrives as an ISO 8601 string carrying an explicit offset, so
// `new Date` resolves an unambiguous instant and the local-time getters below
// re-express it in the browser's zone. That makes the conversion implicit and
// runtime-dependent: safe because the data is fetched client-side, but a caller
// that renders this during prerender would silently show the build machine's
// timezone instead.
//
// Returns an empty string for null/undefined/invalid input so callers can render
// it inline without branching on whether the date was provided.

export function utcToLocal(dateStr?: string | null): string {
  if (!dateStr) return ""
  const d = new Date(dateStr)
  if (isNaN(d.getTime())) return ""
  const yyyy = d.getFullYear()
  const mm = String(d.getMonth() + 1).padStart(2, "0")
  const dd = String(d.getDate()).padStart(2, "0")
  const hh = String(d.getHours()).padStart(2, "0")
  const min = String(d.getMinutes()).padStart(2, "0")
  const ss = String(d.getSeconds()).padStart(2, "0")
  return `${yyyy}-${mm}-${dd} ${hh}:${min}:${ss}`
}

/**
 * How long ago an instant was, in the reader's own clock: "just now", "4m
 * ago", "3h ago", "2d ago", and the local date once it is older than a week.
 *
 * A dashboard is read at a glance, so a wall-clock timestamp ("2026-10-20
 * 14:03:11") makes the reader do the arithmetic. Past a week the relative
 * form stops being useful - "13d ago" stops meaning much - so the date takes
 * over.
 *
 * A timestamp in the future (clock skew between the writing and the reading
 * machine) reads as "just now" rather than as a negative age.
 */
export function relativeTime(dateStr?: string | null): string {
  if (!dateStr) return ""
  const d = new Date(dateStr)
  if (isNaN(d.getTime())) return ""

  const seconds = Math.floor((Date.now() - d.getTime()) / 1000)
  if (seconds < 60) return "just now"

  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`

  const days = Math.floor(hours / 24)
  if (days < 7) return `${days}d ago`

  return utcToLocal(dateStr).slice(0, 10)
}
