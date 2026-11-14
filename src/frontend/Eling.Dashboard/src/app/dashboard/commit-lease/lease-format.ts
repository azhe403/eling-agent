/** Shared age formatting for the lease views — one definition so the card
 * and the detail dialog never drift. */
export function formatAge(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "—"
  const s = Math.floor(seconds)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${s % 60}s`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ${m % 60}m`
  const d = Math.floor(h / 24)
  return `${d}d ${h % 24}h`
}
