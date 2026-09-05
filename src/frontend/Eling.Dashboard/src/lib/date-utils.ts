// Tiny date formatter used by memory list rows. Returns an empty string for
// null/undefined/invalid input so callers can render it inline without
// branching on whether the date was provided.

export function formatDate(dateStr?: string | null): string {
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