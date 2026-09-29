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